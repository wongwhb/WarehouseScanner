using WarehouseScannerCore.Services;
using WarehouseScanner.Services;
using WarehouseScannerCore.Middleware;
using WarehouseScannerCore.Configuration;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using AspNetCoreRateLimit;
using Polly;
using Polly.Extensions.Http;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

// ============== 启动期配置校验 ==============
// 非开发环境缺少 AES 密钥时拒绝启动：密钥缺失会导致已加密的 Epicor 凭据无法解密，
// 应用即使启动也无法完成认证，因此在启动阶段直接 fail-fast。
EncryptionHelper.EnsureKeyMaterialOrThrow(builder.Configuration, !builder.Environment.IsDevelopment());

// ============== Kestrel 请求体大小限制（防止大文件上传耗尽内存） ==============
const long MaxRequestSize = 30 * 1024 * 1024; // 30 MB
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxRequestSize;
});

// ============== CORS 跨域策略 ==============
builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultCors", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if (allowedOrigins != null && allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders("X-CSRF-TOKEN", "X-Request-ID");
        }
        else
        {
            // 未配置 CORS 来源时拒绝所有跨域请求（安全默认，与注释语义一致）
            // 注意：此处绝不能使用 AllowAnyOrigin()——那等于放开任意站点跨域，
            //       与注释声明的"安全默认"完全相反，也会削弱 CSRF 防护。
            // SetIsOriginAllowed(_ => false) 表示不信任任何来源。
            policy.SetIsOriginAllowed(_ => false);
        }
    });
});

// ============== 日志配置（控制台 + 滚动文件） ==============
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("WarehouseScanner", LogLevel.Information);
builder.Logging.AddFilter("WarehouseScannerCore", LogLevel.Information);

string logsDir = Path.Combine(builder.Environment.ContentRootPath, "logs");
builder.Logging.AddProvider(new FileLoggerProvider(logsDir, fileNamePrefix: "warehouse", maxFileSizeBytes: 10 * 1024 * 1024, maxRetainedFiles: 30));

// ============== 配置绑定 ==============
// EpicorSettings 从 appsettings.json 的 "Epicor" 节点读取（LicenseId、超时等）
builder.Services.Configure<EpicorSettings>(
    builder.Configuration.GetSection("Epicor"));

// 速率限制配置
var rateLimitSettings = new RateLimitSettings();
builder.Configuration.GetSection("Security:RateLimiting").Bind(rateLimitSettings);
builder.Services.AddSingleton(rateLimitSettings);

// ============== 原有的服务注册（保留不动） ==============
builder.Services.AddControllersWithViews();

// ============== Data Protection 密钥持久化 ==============
// 持久化到 contentRoot/keys 目录 + 固定应用名，避免重启/容器重建后密钥丢失，
// 导致会话内已加密的 Epicor 凭据无法解密、全员被迫重登。
builder.Services.AddDataProtection()
    .SetApplicationName("WarehouseScanner")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")))
    // DPAPI 本机保护：密钥文件本身再用系统级 DPAPI 加密，防止 keys/ 目录被窃取后直接打开
    // protectToLocalMachine=true：机器级保护（IIS 应用池换账号也能解密）；
    // 反之 protectToLocalMachine=false 仅当前用户可解密，应用池换账号会导致会话全员被迫重登
    .ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddSingleton<ILanguageService, LanguageService>();

builder.Services.AddSingleton<FactoryConfigService>();

builder.Services.AddSingleton<UserMenuPermissionService>();

builder.Services.AddSingleton<IMenuService, MenuService>();

builder.Services.AddSingleton<EncryptionHelper>();

builder.Services.AddScoped<ConfigService>();

// 命名 HttpClient + Polly 弹性策略（重试 + 断路器）
// EpicorApi 命名客户端由 EpicorAuthService 用于 3 种 API 场景（BO / OData / Token）
// BaseAddress 由服务实例动态设置，Timeout 继承 EpicorSettings
// 按"是否可能写"分流为两个客户端（P0-1）：
//   - "EpicorApi"（写）：POST 可能已送达 Epicor，连接被重置时不得重试，否则重复过账；
//   - "EpicorApiIdempotent"（读/幂等）：GET 与 Token 获取，连接层异常可安全重试。
builder.Services.AddHttpClient("EpicorApi")
    // 分层重试职责：本层（Polly）只处理 5xx / 连接层异常；401 刷新 Token 由 EpicorAuthService.Http 应用层负责，
    // 两层触发条件基本不重叠，勿合并或互相替代。
    .AddPolicyHandler((sp, _) => GetRetryPolicy(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Polly.Http"), allowConnectionReset: false))
    .AddPolicyHandler(GetCircuitBreakerPolicy());

builder.Services.AddHttpClient("EpicorApiIdempotent")
    .AddPolicyHandler((sp, _) => GetRetryPolicy(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Polly.Http"), allowConnectionReset: true))
    .AddPolicyHandler(GetCircuitBreakerPolicy());

// 保留默认非命名客户端注册（供其他非 Epicor 用途）
builder.Services.AddHttpClient();

// ====== Polly 策略定义（局部函数） ======

/// <summary>
/// HTTP 调用重试策略（按 HttpMethod + 连接失败性质分流）：
///   - 幂等方法（GET/HEAD/PUT/DELETE/OPTIONS/TRACE）收到 5xx/408 响应 → 重试；
///   - HttpRequestException（连接层异常）→ 仅在**连接根本未建立**时重试；
///     其中 ConnectionReset（连接已建立后被重置）只有在 <paramref name="allowConnectionReset"/>
///     为 true 时才重试——写客户端为 false，避免"请求已送达 Epicor 但连接被重置"时
///     盲目重试造成重复过账；读/幂等客户端为 true，保留连接池陈旧连接的自愈能力。
///   - 不对 TaskCanceledException 重试：无法区分"服务端超时"与"调用方取消"，
///     前者对写操作同样可能已提交，后者重试属于无效开销。
/// </summary>
static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(ILogger logger, bool allowConnectionReset)
{
    return Policy<HttpResponseMessage>
        // HttpRequestException：仅连接未建立场景重试
        .Handle<HttpRequestException>(ex => IsRetryableHttpRequest(ex, allowConnectionReset))
        // HTTP 5xx/408：仅幂等方法重试
        .OrResult(response =>
            IsIdempotentMethod(response.RequestMessage?.Method)
            && ((int)response.StatusCode >= 500 || (int)response.StatusCode == 408))
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(Math.Pow(2, retryAttempt) * 250),
            onRetry: (outcome, timespan, retryAttempt, _) =>
            {
                logger.LogWarning("Polly HTTP 重试 #{RetryAttempt} (等待 {WaitMs}ms)，方法: {Method}，状态码: {StatusCode}，异常: {Exception}",
                    retryAttempt, timespan.TotalMilliseconds,
                    outcome.Result?.RequestMessage?.Method.Method,
                    outcome.Result?.StatusCode,
                    outcome.Exception?.GetType().Name);
            });
}

/// <summary>
/// 判断 HttpRequestException 是否值得重试：
/// InnerException 为 SocketException 且错误码属于"连接根本未建立"时返回 true。
/// ConnectionReset 语义模糊（连接已建立、请求可能已送达），仅当
/// <paramref name="allowConnectionReset"/> 为 true（幂等调用）时才视为可重试。
/// </summary>
static bool IsRetryableHttpRequest(HttpRequestException ex, bool allowConnectionReset)
    => ex.InnerException is System.Net.Sockets.SocketException se
    && (se.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
            or System.Net.Sockets.SocketError.HostUnreachable
            or System.Net.Sockets.SocketError.ConnectionRefused
        || (allowConnectionReset && se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset));

/// <summary>幂等 HTTP 方法判定：只有这些方法允许在收到 5xx/408 后重试</summary>
static bool IsIdempotentMethod(HttpMethod? method)
    => method == HttpMethod.Get || method == HttpMethod.Head
    || method == HttpMethod.Options || method == HttpMethod.Put
    || method == HttpMethod.Delete || method == HttpMethod.Trace;

/// <summary>断路器策略：30 秒内连续 5 次失败则熔断 15 秒</summary>
static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .AdvancedCircuitBreakerAsync(
            failureThreshold: 0.5,           // 50% 失败率触发熔断
            samplingDuration: TimeSpan.FromSeconds(30),
            minimumThroughput: 5,             // 最少 5 次请求
            durationOfBreak: TimeSpan.FromSeconds(15));
}

// 注册 IMemoryCache（供 IssueBusinessService 等使用，支持幂等性缓存 TTL）
// 注意：不设置 SizeLimit 以避免要求每个缓存条目都显式设置 Size（否则会抛 InvalidOperationException）
builder.Services.AddMemoryCache(options =>
{
    options.ExpirationScanFrequency = TimeSpan.FromMinutes(1); // 1 分钟扫描一次过期项
});

// EpicorAuthServiceFactory 依赖 IHttpClientFactory 和 IOptions<EpicorSettings>
builder.Services.AddSingleton<IEpicorAuthServiceFactory, EpicorAuthServiceFactory>();

// 后台定时清理过期的 EpicorAuthService 缓存实例（空闲实例主动驱逐）
builder.Services.AddHostedService<EpicorSessionCleanupService>();

// IHttpContextAccessor：服务层通过它访问 HttpContext.Session（工厂模式创建 EpicorAuthService）
builder.Services.AddHttpContextAccessor();

// ============== 发料业务服务注册（分层架构） ==============
// EpicorQueryGateway：通过 IEpicorAuthServiceFactory + IHttpContextAccessor 动态创建 authService
builder.Services.AddScoped<IEpicorQueryGateway, EpicorQueryGateway>();
// EpicorBusinessServiceFactory：基于 Session 动态创建 EpicorIssueService，供 IssueBusinessService 使用
builder.Services.AddScoped<IEpicorBusinessServiceFactory, EpicorBusinessServiceFactory>();

// 业务级并发锁 + 幂等性缓存统一提供者（Singleton，全局共享锁字典与清理定时器）
// 消除 ReturnController / TransferController / OutboundController / BaseBusinessService 之间的重复锁代码
builder.Services.AddSingleton<IBusinessLockProvider, BusinessLockProvider>();

// 每日单据操作明细日志服务：将每次提交的单据（成功/失败）按天写入 logs/单据操作明细_yyyyMMdd.txt
builder.Services.AddSingleton<DocumentOperationLogService>();

// 速率限制服务注册
if (rateLimitSettings.EnableRateLimiting)
{
    // IP 速率限制（IMemoryCache 已在前面统一注册，此处直接复用同一实例）
    builder.Services.Configure<IpRateLimitOptions>(options =>
    {
        options.EnableEndpointRateLimiting = true;
        options.StackBlockedRequests = false;
        // 限流身份统一使用 HttpContext.Connection.RemoteIpAddress（与登录日志一致），
        //   不读取 X-Real-IP：该头完全由客户端控制，任意伪造即可绕过限流。
        //   反向代理部署需显式开启 ReverseProxy:Enabled=true 并配置受信代理，见下方 UseForwardedHeaders。
        options.RealIpHeader = string.Empty;
        // ClientIdHeader 置空：X-Client-Id 完全由客户端控制，任意伪造即可绕过限流；
        //   已确认前端与所有调用方均不发送该头，置空后限流身份只由 IP 决定，行为不变。
        options.ClientIdHeader = string.Empty;
        options.HttpStatusCode = 429; // Too Many Requests
        
        // 规则的命中与取舍由 AspNetCoreRateLimit 内部决定，与声明顺序无关：
        //   - 请求端点按 "verb:path" 全串匹配（两侧均转小写，故大小写不敏感，路径末尾斜杠被裁剪）；
        //   - 同 Period 命中的多条规则会被归并，只保留 Limit 最小（最严格）的那条，不会叠加生效。
        // 因此登录 POST 同时命中登录规则与 "*"，最终按前者（LoginRequestsPerMinute）执行。
        options.GeneralRules = new List<RateLimitRule>
        {
            // 登录接口单独限流：防止暴力破解/撞库（默认 10 次/分钟，见 Security:RateLimiting:LoginRequestsPerMinute）
            // 注意：登录表单的 action 是 Url.Action("Login","Account")，默认路由
            //       {controller=Account}/{action=Login} 会把与默认值相同的段消减掉，真实提交路径是 "/"，
            //       因此必须同时声明 "post:/"；只声明 "post:/Account/Login" 时该规则永不命中，
            //       登录实际只剩通用规则的 ApiRequestsPerMinute（实测可连续提交 120 次/分钟）。
            new RateLimitRule
            {
                Endpoint = "post:/",
                Period = "1m",
                Limit = rateLimitSettings.LoginRequestsPerMinute
            },
            // 直接访问 /Account/Login 的调用方（脚本/第三方）同样受限，避免绕过
            new RateLimitRule
            {
                Endpoint = "post:/Account/Login",
                Period = "1m",
                Limit = rateLimitSettings.LoginRequestsPerMinute
            },
            // 其它接口通用规则：每分钟 ApiRequestsPerMinute 次请求
            new RateLimitRule
            {
                Endpoint = "*",
                Period = "1m",
                Limit = rateLimitSettings.ApiRequestsPerMinute
            }
        };
        
        // 白名单 IP
        if (rateLimitSettings.IpWhitelist != null && rateLimitSettings.IpWhitelist.Length > 0)
        {
            options.IpWhitelist = rateLimitSettings.IpWhitelist.ToList();
        }
    });

    builder.Services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
    builder.Services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
    builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
    builder.Services.AddSingleton<IProcessingStrategy, AsyncKeyLockProcessingStrategy>();
}

// IssueBusinessService：依赖 EpicorQueryGateway + IEpicorBusinessServiceFactory，不直接依赖 EpicorAuthService
builder.Services.AddScoped<IssueBusinessService>();

// Epicor 数据访问统一使用 EpicorAuthService + EpicorQueryGateway 路径

// ReturnBusinessService：物料退回业务服务
builder.Services.AddScoped<ReturnBusinessService>();
builder.Services.AddScoped<TransferBusinessService>();
builder.Services.AddScoped<OutboundBusinessService>();

// ReceiptBusinessService：工单入库业务服务
builder.Services.AddScoped<ReceiptBusinessService>();

builder.Services.AddScoped<ShippingScheduleQueryService>();

// 鉴权真源统一为 Session —— Cookie 认证管线已整体移除
// 移除原因：Cookie 票据只签发、从未参与任何鉴权判定（全文无 [Authorize]、无 ClaimsPrincipal 校验），
//           而 IsUserAuthenticated / RequirePermissionAttribute / CheckPagePermission 读的都是 Session，
//           属"双机制冗余"，且角色/身份的 Cookie 回退可被客户端伪造。
// 此处仅保留授权服务注册，供 app.UseAuthorization() 使用。
builder.Services.AddAuthorization();

// ====== 分布式缓存选择（Redis > Memory） ======
var redisConnectionString = builder.Configuration.GetValue<string>("Redis:ConnectionString");
if (!string.IsNullOrEmpty(redisConnectionString))
{
    // 生产环境：Redis 分布式缓存（Session + 速率限制在多实例间共享）
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = builder.Configuration.GetValue<string>("Redis:InstanceName") ?? "WarehouseScanner";
    });

    // 速率限制存储已在统一位置注册（MemoryCacheIpPolicyStore 等）
}
else
{
    // 开发/单实例：内存分布式缓存
    builder.Services.AddDistributedMemoryCache();

    // 速率限制存储已在初始化块中注册（MemoryCacheIpPolicyStore 等）
}

// Cookie 安全策略：防伪造令牌 Cookie 与 Session Cookie 共用同一策略。
// 生产环境强制 HTTPS（Always），开发环境放行 HTTP 便于本地调试（SameAsRequest）。
CookieSecurePolicy cookieSecurePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;

// 配置防伪造令牌（CSRF 防护）
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = cookieSecurePolicy;
});

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(1);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = cookieSecurePolicy;
});

var app = builder.Build();

// ============== 反向代理支持（默认关闭） ==============
// 仅当配置 ReverseProxy:Enabled=true 时启用 ForwardedHeaders：
//   默认不信任 X-Forwarded-* / X-Real-IP，避免外部客户端伪造来源 IP 绕过限流或污染登录日志。
//   启用后仍只采信受信代理（ForwardedHeadersOptions 默认仅信任回环地址），
//   跨机部署需另行配置 KnownProxies / KnownNetworks。
if (builder.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });
}

#region 启动阶段异步预热工厂配置
// 目的：让请求路径上的同步 EnsureLoaded() 只做内存命中，不触发同步文件 I/O / 阻塞请求线程。
// 预热失败不影响启动（后续请求仍可按需懒加载）。
try
{
    await app.Services.GetRequiredService<FactoryConfigService>().WarmUpAsync();
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "工厂配置预热调用失败（非致命，后续请求将按需懒加载）");
}
#endregion

#region 管道前段异常兜底（最外层）
// 说明：GlobalExceptionMiddleware 注册在 UseRouting() 之后（见下文），以便读取已解析的端点元数据；
// 因此 UseRouting 之前各中间件（CORS / 静态文件 / Session 等）抛出的异常需要这一层兜底，
// 否则异常会直接裸露给 Kestrel（连接被重置，客户端收不到任何可读信息）。
// 本层只做通用 500 应答，不做业务异常映射——业务映射（401 会话失效 / 400 参数错误等）由内层
// GlobalExceptionMiddleware 负责，异常会先被内层捕获，正常不会走到这里。
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    if (context.Response.HasStarted) return;

    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    app.Logger.LogError(error, "管道前段未处理异常: {Path}", context.Request.Path);

    context.Response.Clear();
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsync("{\"success\":false,\"message\":\"服务器内部错误，请稍后重试\",\"code\":500}");
}));
#endregion

#region 安全响应头中间件（防 XSS / Clickjacking / MIME嗅探）
app.UseMiddleware<SecurityHeadersMiddleware>();
#endregion

// 速率限制中间件（在所有路由之前）
if (rateLimitSettings.EnableRateLimiting)
{
    app.UseIpRateLimiting();
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    // 注意：此处**不**注册 UseExceptionHandler("/Home/Error")。
    // 它注册在本位置属"内层"，会先于 GlobalExceptionMiddleware 捕获异常并重放 /Home/Error，
    // 把 401（会话失效）/ 400（参数/防伪令牌）等业务语义统一降级为 500 + HTML 错误页，
    // 导致前端拿不到 errorCode、无法引导操作员重新登录。
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors("DefaultCors"); // CORS 中间件（在路由 / 静态文件之前）
app.UseStaticFiles();
app.UseSession();
app.UseRouting();

// 全局异常处理中间件（业务异常语义映射）
// 注册在 UseRouting() 之后：RequiresAntiforgeryValidation 依赖 context.GetEndpoint()，
// 只有路由已解析时才能区分"防伪令牌失效导致的空 400"与"其它原因的空 400"。
// UseRouting 之前的管道异常由文件上方的 UseExceptionHandler 兜底。
app.UseMiddleware<GlobalExceptionMiddleware>();

// 注意：不使用 app.UseAntiforgery() 中间件。
// 原因：它由 AntiforgeryMiddleware 在管道内部短路返回 400，全局异常中间件拿不到可读原因。
//       改为在 POST 方法上标注 [ValidateAntiForgeryToken] Filter。
//       实测（.NET 8）：ValidateAntiforgeryTokenAuthorizationFilter 会自行捕获
//       AntiforgeryValidationException、写日志并把状态码置为 400，且不写响应体，
//       异常不会冒泡到 GlobalExceptionMiddleware 的 catch 分支。
//       因此由 GlobalExceptionMiddleware 在 _next 返回后，依据
//       「POST + 400 + 响应未开始写 + 端点元数据声明了防伪校验」这一组合来补救：
//       AJAX 请求补写带 errorCode 的 JSON，页面请求 302 重定向到登录页。
//       @Html.AntiForgeryToken() 仍可正常生成 Token 并设置 Cookie。

// 鉴权真源统一为 Session
//   - IsUserAuthenticated / GetCurrentUserId / RequirePermissionAttribute 读的都是 SessionKeys.UserId / UserRole
//   - 未登录判定（Session 无 UserId）由各 Controller 与 RequirePermission 自行处理，AJAX 返回 401 JSON
//   - 因此不需要 UseAuthentication()：没有注册任何认证方案，调用它只会做无用功
app.UseAuthorization();

// ============== 健康检查终结点（K8s / 负载均衡器 / 监控探针） ==============
// Liveness Probe：容器运行时存活检查（轻量，不检查外部依赖）
app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "Healthy",
        service = "WarehouseScanner",
        timestamp = DateTimeOffset.UtcNow
    }));

// Readiness Probe：服务就绪检查（可扩展：数据库、Epicor API 连通性）
app.MapGet("/health/ready", () =>
    Results.Ok(new
    {
        status = "Ready",
        service = "WarehouseScanner",
        timestamp = DateTimeOffset.UtcNow
    }));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();