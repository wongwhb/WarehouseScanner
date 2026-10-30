// Filters/RequirePermissionAttribute.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Filters
{
    /// <summary>
    /// 页面 / 接口权限校验特性。
    ///
    /// 用法：
    ///   [RequirePermission(MenuCodes.Issue)]     // 加在 Controller 上 → 其全部 Action 生效
    ///   [RequirePermission(MenuCodes.Settings)]  // 加在单个 Action 上 → 仅该 Action 生效
    ///
    /// 行为约定（与既有手写 CheckPagePermission 语义一致，但失败时一律关闭）：
    ///   1. 未登录                  → 页面请求重定向登录页；AJAX 请求返回 401 JSON
    ///   2. 已登录但无菜单权限       → 页面请求返回 AccessDenied 视图；AJAX 请求返回 403 JSON
    ///   3. 权限服务不可用（配置错误）→ 失败关闭（拒绝访问），绝不静默放行
    ///
    /// 设计说明：实现为 IAsyncAuthorizationFilter，在模型绑定与 Action 执行之前运行，
    /// 因此无权限的请求不会进入 Action 体，也不会有任何业务逻辑被执行。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter
    {
        private readonly string _menuCode;

        public RequirePermissionAttribute(string menuCode)
            => _menuCode = string.IsNullOrWhiteSpace(menuCode)
                ? throw new ArgumentNullException(nameof(menuCode))
                : menuCode;

        public Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var http = context.HttpContext;
            var logger = http.RequestServices
                .GetService<ILoggerFactory>()?.CreateLogger<RequirePermissionAttribute>();

            // ---------- 1. 登录检查 ----------
            var userId = http.Session.GetString(SessionKeys.UserId);
            if (string.IsNullOrEmpty(userId))
            {
                logger?.LogWarning("未登录用户尝试访问受保护资源: {MenuCode}, Path={Path}",
                    _menuCode, http.Request.Path);

                context.Result = IsAjaxRequest(http.Request)
                    ? BuildJsonError(StatusCodes.Status401Unauthorized, "会话已过期，请重新登录")
                    : new RedirectToActionResult("Login", "Account", null);
                return Task.CompletedTask;
            }

            // ---------- 2. 权限检查 ----------
            var permissionService = http.RequestServices.GetService<UserMenuPermissionService>();

            if (permissionService == null)
            {
                // 失败关闭：配置错误时必须拒绝，而不是放行
                logger?.LogError("权限服务未注册，按失败关闭策略拒绝访问: {MenuCode}", _menuCode);
                context.Result = BuildDeniedResult(context);
                return Task.CompletedTask;
            }

            // 角色只信任服务端 Session：禁止回退到客户端可写的 Cookie（可被伪造，存在越权风险）。
            var userRole = http.Session.GetString(SessionKeys.UserRole) ?? string.Empty;
            // 权限按站点隔离：同一账号在不同站点可有不同菜单权限
            var siteCode = http.Session.GetString(SessionKeys.SiteCode) ?? string.Empty;

            bool allowed;
            try
            {
                allowed = permissionService.HasPermission(userId, userRole, _menuCode, siteCode);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "权限校验异常，按失败关闭策略拒绝访问: {MenuCode}", _menuCode);
                allowed = false;
            }

            // ---------- 3. 结果处理 ----------
            if (!allowed)
            {
                logger?.LogWarning("用户 {UserId} (角色 {UserRole}) 无权限访问菜单 {MenuCode}, Path={Path}",
                    userId, userRole, _menuCode, http.Request.Path);
                context.Result = BuildDeniedResult(context);
            }

            return Task.CompletedTask;
        }

        /// <summary>构造拒绝访问的响应：AJAX 返回 403 JSON，页面返回 AccessDenied 视图</summary>
        private static IActionResult BuildDeniedResult(AuthorizationFilterContext context)
        {
            var http = context.HttpContext;
            if (IsAjaxRequest(http.Request))
                return BuildJsonError(StatusCodes.Status403Forbidden, "您没有权限执行此操作");

            // 显式提供 ViewData，避免依赖框架对 null ViewData 的隐式补齐
            var metadataProvider = http.RequestServices.GetRequiredService<IModelMetadataProvider>();
            return new ViewResult
            {
                ViewName = "AccessDenied",
                ViewData = new ViewDataDictionary(metadataProvider, context.ModelState)
            };
        }

        private static JsonResult BuildJsonError(int statusCode, string message)
            => new(new { success = false, message })
            {
                StatusCode = statusCode
            };

        /// <summary>判断是否为 AJAX / JSON 请求（决定返回 401/403 JSON 还是重定向/错误页）</summary>
        private static bool IsAjaxRequest(HttpRequest request)
        {
            if (string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 兼容未设置 X-Requested-With 但声明期望 JSON 的调用方
            var accept = request.Headers.Accept.ToString();
            return !string.IsNullOrEmpty(accept)
                && accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        }
    }
}
