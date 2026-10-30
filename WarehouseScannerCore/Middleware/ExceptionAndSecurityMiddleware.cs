using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Threading.Tasks;

namespace WarehouseScannerCore.Middleware
{
    #region 全局异常处理中间件
    /// <summary>
    /// 全局异常处理中间件
    /// 职责：捕获所有未处理异常，统一日志记录和响应格式
    /// 对 AJAX 请求返回 JSON，对页面请求返回重定向
    /// </summary>
    public class GlobalExceptionMiddleware
    {
        /// <summary>会话过期统一文案（与前端 handleSessionIfExpired 的识别文案对应）</summary>
        private const string SessionExpiredMessage = "登录已过期，请重新登录";

        /// <summary>登录态变更（防伪令牌失效）统一文案</summary>
        private const string SessionChangedMessage = "登录状态已变更，请重新登录";

        /// <summary>机器可读错误码：会话过期。前端据此判定需要跳登录页（与界面语言无关）</summary>
        private const string ErrorCodeSessionExpired = "SessionExpired";

        /// <summary>机器可读错误码：登录态变更（防伪令牌失效）</summary>
        private const string ErrorCodeSessionChanged = "SessionChanged";

        /// <summary>端点未声明防伪校验、却返回了空 400 时的兜底文案（不可视为登录态失效）</summary>
        private const string InvalidRequestMessage = "请求无效，请刷新页面后重试";

        /// <summary>JSON 错误响应序列化选项（静态缓存，避免每次写入重复实例化，P3-consistency）</summary>
        private static readonly JsonSerializerSettings JsonErrorOptions = new()
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        };

        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionMiddleware> _logger;

        public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);

                // [ValidateAntiForgeryToken] Filter 校验失败时不抛异常，而是设置 StatusCode=400。
                // 此时需要区分：
                //   1. 登录路径（/ 或 /Account/Login）：LoginAsync 已手动处理 Token 校验，
                //      不会再出现 Filter 导致的 400。但保留作为安全网。
                //   2. 其他路径（如 /plan）：主页的 POST 请求 Token 失效，需重定向到登录页。
                // 页面请求 → 302 重定向登录页；AJAX 请求 → 返回统一 JSON 错误体
                // （Filter 不写响应体，若不补写，前端只能看到"HTTP 400"且无法识别为登录态失效）。
                if (context.Response.StatusCode == (int)HttpStatusCode.BadRequest
                    && !context.Response.HasStarted
                    && string.Equals(context.Request.Method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase))
                {
                    string path = context.Request.Path.Value!;

                    // 登录相关路径：LoginAsync 已手动处理，这里作为安全网
                    if (path == "/" || path.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("登录路径 AntiForgery Token 校验失败，重定向: {Path}", path);
                        context.Response.Redirect("/Account/Login?error=" + Uri.EscapeDataString(SessionChangedMessage));
                    }
                    // 其他页面路径：只有「该端点确实声明了防伪校验」时，空 400 才能被解释为令牌失效。
                    // 实测（.NET 8）：[ValidateAntiForgeryToken] 由 ValidateAntiforgeryTokenAuthorizationFilter
                    // 内部捕获 AntiforgeryValidationException 后设置 400 且不写响应体，异常不会冒泡到本中间件的
                    // catch 分支，因此"空 400"是这条路线唯一可观测的信号。若不加以区分，任何原因的空 400
                    // （例如将来某个 Action 直接 return BadRequest()）都会被误标成"登录状态已变更"，
                    // 前端据此会把操作员强制踢回登录页。
                    else if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isAjax = context.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                                    || context.Request.Headers.Accept.ToString().Contains("application/json");

                        if (RequiresAntiforgeryValidation(context))
                        {
                            if (!isAjax)
                            {
                                _logger.LogWarning("页面请求 AntiForgery Token 校验失败，重定向: {Path}", path);
                                context.Response.Redirect("/Account/Login?error=" + Uri.EscapeDataString(SessionChangedMessage));
                            }
                            else
                            {
                                // AJAX 请求：Filter 校验失败只把状态码置为 400、不写响应体，
                                // 前端只能看到 "HTTP 400"，既显示不出原因，也无法识别为登录态失效（因而不跳登录页）。
                                // 这里补写统一 JSON 错误体（含 errorCode，与界面语言无关）。
                                _logger.LogWarning("AJAX 请求 AntiForgery Token 校验失败，返回 JSON 错误: {Path}", path);
                                await WriteJsonError(context, HttpStatusCode.BadRequest, SessionChangedMessage, ErrorCodeSessionChanged);
                            }
                        }
                        else if (isAjax)
                        {
                            // 端点未声明防伪校验却返回了空 400：不当作登录态失效（否则会误踢回登录页），
                            // 但仍补写 JSON 体，避免前端只能看到 "HTTP 400" 这种技术性状态码。
                            _logger.LogWarning("端点返回空 400（非防伪校验失败），返回 JSON 提示: {Path}", path);
                            await WriteJsonError(context, HttpStatusCode.BadRequest, InvalidRequestMessage);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var (statusCode, message, logLevel, redirectUrl, errorCode) = ex switch
                {
                    AntiforgeryValidationException => (
                        HttpStatusCode.BadRequest,
                        SessionChangedMessage,
                        LogLevel.Warning,
                        $"/Account/Login?error={Uri.EscapeDataString(SessionChangedMessage)}",
                        ErrorCodeSessionChanged),

                    UnauthorizedAccessException => (
                        HttpStatusCode.Unauthorized,
                        SessionExpiredMessage,
                        LogLevel.Warning,
                        $"/Account/Login?error={Uri.EscapeDataString(SessionExpiredMessage)}",
                        ErrorCodeSessionExpired),

                    // 业务类 ArgumentException（无 ParamName）→ 透传具体业务提示，避免用户只看到泛化文案；
                    // 参数校验类 ArgumentException（带 ParamName）→ 统一泛化提示。
                    ArgumentException aex when string.IsNullOrEmpty(aex.ParamName) => (
                        HttpStatusCode.BadRequest,
                        aex.Message,
                        LogLevel.Warning,
                        (string?)null,
                        (string?)null),

                    ArgumentException => (
                        HttpStatusCode.BadRequest,
                        "请求参数无效",
                        LogLevel.Warning,
                        (string?)null,
                        (string?)null),

                    _ => (
                        HttpStatusCode.InternalServerError,
                        "服务器内部错误，请稍后重试",
                        LogLevel.Error,
                        (string?)null,
                        (string?)null)
                };

                _logger.Log(logLevel, ex, "请求处理异常: {Path}, TraceIdentifier: {TraceIdentifier}",
                    context.Request.Path, context.TraceIdentifier);

                if (!string.IsNullOrEmpty(redirectUrl))
                {
                    bool isAjax = context.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                                  || context.Request.Headers.Accept.ToString().Contains("application/json");

                    if (isAjax)
                    {
                        await WriteJsonError(context, statusCode, message, errorCode);
                    }
                    else
                    {
                        context.Response.Redirect(redirectUrl);
                    }
                    return;
                }

                await WriteErrorResponse(context, statusCode, message, errorCode);
            }
        }

        /// <summary>
        /// 判断当前请求匹配到的端点是否声明了防伪令牌校验
        /// （[ValidateAntiForgeryToken] / [AutoValidateAntiforgeryToken]）。
        /// 用途：区分"防伪令牌校验失败导致的空 400"与"其它原因的空 400"，
        /// 避免把后者误判为登录态失效。
        /// 说明：.NET 8 下 ValidateAntiForgeryTokenAttribute 会出现在端点元数据里，
        ///       但它不实现 IAntiforgeryMetadata，需按特性类型直接取。
        /// </summary>
        private static bool RequiresAntiforgeryValidation(HttpContext context)
        {
            var endpoint = context.GetEndpoint();
            if (endpoint == null) return false;

            return endpoint.Metadata.GetMetadata<ValidateAntiForgeryTokenAttribute>() != null
                || endpoint.Metadata.GetMetadata<AutoValidateAntiforgeryTokenAttribute>() != null;
        }

        private static Task WriteErrorResponse(HttpContext context, HttpStatusCode statusCode, string message, string? errorCode = null)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.StatusCode = (int)statusCode;

            bool isAjax = context.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                         || context.Request.Headers.Accept.ToString().Contains("application/json");

            if (isAjax)
            {
                return WriteJsonError(context, statusCode, message, errorCode);
            }

            // 页面请求统一跳转通用错误页：不把异常详情带进 URL（避免泄露内部信息/被日志采集留存），
            // 异常细节只在上方日志中按 TraceIdentifier 记录，供排查时关联。
            context.Response.Redirect("/Home/Error");
            return Task.CompletedTask;
        }

        private static Task WriteJsonError(HttpContext context, HttpStatusCode statusCode, string message, string? errorCode = null)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.StatusCode = (int)statusCode;

            var response = new
            {
                success = false,
                message = message,
                // code 保留为 HTTP 状态码（历史行为，前端部分逻辑依赖）；
                // errorCode 为机器可读的业务错误码，前端据此判定"会话失效/登录态变更"，
                // 避免依赖随界面语言变化的 message 文案。
                code = (int)statusCode,
                errorCode = errorCode
            };
            var json = JsonConvert.SerializeObject(response, JsonErrorOptions);
            return context.Response.WriteAsync(json);
        }
    }
    #endregion

    #region 安全响应头中间件
    /// <summary>
    /// 安全响应头中间件
    /// 职责：统一设置防 XSS/Clickjacking/MIME 嗅探等安全响应头
    /// </summary>
    public class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IWebHostEnvironment _env;

        public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _env = env ?? throw new ArgumentNullException(nameof(env));
        }

        public Task InvokeAsync(HttpContext context)
        {
            context.Response.OnStarting(() =>
            {
                // 基础安全响应头
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                // SAMEORIGIN 与 CSP frame-ancestors 'self' 语义保持一致
                context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                // 不输出 X-XSS-Protection：现代浏览器已废弃该头，开启反而可能在旧浏览器引入风险
                context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";

                // CSP 策略优化
                var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

                // 开发环境判定必须基于宿主环境而非请求 Host 头 —— Host 由客户端控制，
                // 用 Request.Host 判断会让攻击者通过 "Host: localhost" 抑制 CSP 响应头
                var isDevelopment = _env.IsDevelopment();
                
                if (!isDevelopment)
                {
                    // 生产环境：CSP 策略允许内联脚本（项目中大量视图使用内联 script 和 onclick）
                    // 注意：CSP 规范中 nonce 与 'unsafe-inline' 同时存在时 'unsafe-inline' 会被忽略，
                    // 因此此处保留 'unsafe-inline' 以使所有现有页面正常工作（nonce 不生效）。
                    //
                    // 【已知接受风险登记】script-src 'unsafe-inline'（XSS 缓解依赖输出编码与防伪校验）
                    // - 登记日期：2026-10-21
                    // - 风险说明：内联脚本放行削弱了 CSP 对注入脚本的阻断能力
                    // - 接受理由：系统为内网部署，全部 POST 均启用 ValidateAntiForgeryToken，
                    //   输出经 Razor 默认 HTML 编码；现有 28 个视图、82 个 <script> 中 26 个内联脚本
                    //   与 6 处内联事件处理器（onclick/onchange）需一次性迁移到 nonce /
                    //   addEventListener，遗漏任一处将导致该页面 JS 被浏览器静默阻断，回归面大
                    // - 后续计划：前端框架化改造时统一迁移，完成后移除 'unsafe-inline'
                    var connectSrc = "'self'";
                    var scriptSrc = "'self' 'unsafe-inline'";

                    context.Response.Headers["Content-Security-Policy"] =
                        $"default-src 'self'; " +
                        $"connect-src {connectSrc}; " +
                        $"script-src {scriptSrc}; " +
                        $"style-src 'self' 'unsafe-inline'; " +  // 允许内联样式
                        $"img-src 'self' data: blob:; " +  // 允许 data: 和 blob: 图片
                        $"font-src 'self' data:; " +  // 允许字体加载
                        $"frame-ancestors 'self'";  // 允许同域嵌入，更实用
                }
                // 开发环境不设置 CSP，允许所有资源加载

                // 将 nonce 传递给 View（供 Razor 视图使用，保留兼容性）
                context.Items["CspNonce"] = nonce;
                
                return Task.CompletedTask;
            });

            return _next(context);
        }
    }
    #endregion
}
