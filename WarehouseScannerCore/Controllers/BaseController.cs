using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using WarehouseScanner.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    #region 基础控制器
    /// <summary>
    /// 基础控制器（优化版）
    /// 职责：
    ///   1. 统一注入通用依赖（Logger、DataProtection、EpicorFactory、Cache、语言服务、权限服务）
    ///   2. 统一会话 / 权限 / 语言读取
    ///   3. 提供给所有业务控制器一致的基类
    /// </summary>
    public class BaseController : Controller
    {
        protected readonly ILogger _logger;
        protected readonly IDataProtector _protector;
        protected readonly IEpicorAuthServiceFactory _epicorServiceFactory;
        protected readonly IMemoryCache _memoryCache;
        protected readonly IWebHostEnvironment _environment;
        protected readonly Services.ILanguageService? _languageService;
        protected readonly Services.UserMenuPermissionService _permissionService;

        public BaseController(
            ILogger logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IMemoryCache memoryCache,
            Services.ILanguageService? languageService = null,
            Services.UserMenuPermissionService? permissionService = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _protector = (protectionProvider ?? throw new ArgumentNullException(nameof(protectionProvider)))
                .CreateProtector("AccountController.SensitiveData");
            _epicorServiceFactory = epicorServiceFactory ?? throw new ArgumentNullException(nameof(epicorServiceFactory));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _environment = environment;
            _languageService = languageService;

            // 权限服务是必填依赖。DI 必须注册 UserMenuPermissionService，
            // 否则控制器构造即抛异常（启动/首次请求阶段快速失败），不会让权限校验被静默跳过。
            _permissionService = permissionService
                ?? throw new ArgumentNullException(nameof(permissionService),
                    "权限服务 (UserMenuPermissionService) 必须注入；缺失会导致页面权限校验被静默跳过。");
        }

        #region 认证与会话
        /// <summary>
        /// 工厂配置服务（Singleton，按请求从容器解析）。
        /// 站点 → 公司/工厂解析的唯一事实来源，BaseController 各子类共用，避免各自注入不一致。
        /// </summary>
        protected WarehouseScannerCore.Services.FactoryConfigService FactoryConfigService
            => HttpContext.RequestServices.GetRequiredService<WarehouseScannerCore.Services.FactoryConfigService>();

        /// <summary>
        /// 当前租户键：站点|公司|工厂（小写）。
        /// 应用级缓存/业务锁/幂等键一律用它做前缀，否则多站点会互相命中或互相阻塞。
        /// 公司/工厂与认证实例创建同源解析，新旧会话派生结果一致。
        /// </summary>
        protected string CurrentTenantKey
            => EpicorSessionHelper.GetTenantKey(HttpContext.Session, FactoryConfigService);

        /// <summary>从 Session 创建 EpicorAuthService（复用统一工厂路径）</summary>
        protected virtual EpicorAuthService CreateEpicorAuthServiceFromSession()
        {
            var session = HttpContext.Session;
            var username = EpicorSessionHelper.GetUsername(session);
            if (string.IsNullOrEmpty(username))
                throw new UnauthorizedAccessException("用户未登录或会话已过期");

            return _epicorServiceFactory.CreateFromSession(session, _protector);
        }

        /// <summary>判断用户是否已登录</summary>
        protected virtual bool IsUserAuthenticated()
        {
            return HttpContext.Session.GetString(SessionKeys.UserId) != null;
        }

        /// <summary>获取当前登录用户名</summary>
        protected virtual string GetCurrentUserName() => EpicorSessionHelper.GetUsername(HttpContext.Session);

        /// <summary>获取当前登录用户 ID</summary>
        protected virtual string GetCurrentUserId()
            => HttpContext.Session.GetString(SessionKeys.UserId) ?? string.Empty;
        #endregion

        #region 语言资源
        /// <summary>
        /// 从语言服务取本地化文本。统一委托到 <see cref="GetLocalText"/>，消除与
        /// GetLocalText 重复的 GetString + 括号回退逻辑。
        /// </summary>
        protected virtual string GetText(string category, string key, string? languageCode = null)
            => GetLocalText(category, key, languageCode);

        /// <summary>获取当前请求的语言代码（Cookie → 浏览器 Accept-Language → 默认）</summary>
        protected virtual string GetCurrentLanguage()
            => Request.Cookies[CookieNames.Language] ?? AppDefaults.DefaultLanguage;

        /// <summary>
        /// 从已加载的资源字典中查找文本（优先），未命中则回退到语言服务。
        /// 这是各子控制器中重复 GetText local function 的统一实现。
        /// </summary>
        /// <param name="section">资源小节名</param>
        /// <param name="key">键名</param>
        /// <param name="cultureCode">语言代码</param>
        /// <param name="preloadedResources">可选：已加载的完整资源字典（用于 O(1) 查找）</param>
        protected virtual string GetLocalText(string section, string key, string? cultureCode = null,
            Dictionary<string, Dictionary<string, string>>? preloadedResources = null)
        {
            var lang = cultureCode ?? GetCurrentLanguage();

            // 先查预加载字典（Action 顶层一次性加载）
            if (preloadedResources != null
                && preloadedResources.TryGetValue(section, out var sectionResources)
                && sectionResources.TryGetValue(key, out var value))
            {
                return value;
            }

            // 回退到语言服务：以"资源是否存在"判定，而不是比较 "[...]" 字符串形状
            // （合法文案可能以 '[' 开头，旧的 StartsWith/EndsWith 判断会误判为缺失）
            if (_languageService != null &&
                _languageService.TryGetString(lang, section, key, out var text) &&
                !string.IsNullOrEmpty(text))
            {
                return text;
            }
            return GetDefaultLocalizedFallback(lang);
        }

        /// <summary>
        /// 资源缺失时的可读兜底文案（不向界面输出 "[section.key]" 占位符）。
        /// </summary>
        protected virtual string GetDefaultLocalizedFallback(string languageCode)
        {
            if (_languageService != null &&
                _languageService.TryGetString(languageCode, "Common", "OperationFailedRetry", out var fallback) &&
                !string.IsNullOrEmpty(fallback))
            {
                return fallback;
            }
            return "操作失败，请稍后重试";
        }

        /// <summary>
        /// 创建可放入 ViewBag.GetText 的委托，封装了语言获取逻辑。
        /// 替代各子控制器中重复的 local function 定义。
        /// </summary>
        protected virtual Func<string, string, string> CreateGetTextFunc(
            string? cultureCode = null,
            Dictionary<string, Dictionary<string, string>>? preloadedResources = null)
        {
            var lang = cultureCode ?? GetCurrentLanguage();
            // 捕获当前 lang 和 resource，避免每次委托调用都查 Cookie
            return (section, key) => GetLocalText(section, key, lang, preloadedResources);
        }

        /// <summary>
        /// 统一设置 ViewBag 本地化/用户信息（替代各子控制器 Index 中重复的 ViewBag.GetText / CurrentCulture / UserId 等赋值）
        /// </summary>
        /// <param name="preloadedResources">可选：预加载的完整资源字典</param>
        protected virtual void SetupViewBagLocalization(
            Dictionary<string, Dictionary<string, string>>? preloadedResources = null)
        {
            var cultureName = GetCurrentLanguage();

            ViewBag.GetText = CreateGetTextFunc(cultureName, preloadedResources);
            ViewBag.CurrentCulture = cultureName;
            ViewBag.CurrentUICulture = cultureName;
            ViewBag.UserId = HttpContext.Session.GetString(SessionKeys.UserId) ?? string.Empty;
            ViewBag.UserRole = HttpContext.Session.GetString(SessionKeys.UserRole) ?? string.Empty;
            ViewBag.SiteName = EpicorSessionHelper.GetSiteCode(HttpContext.Session);
            ViewBag.LoginTime = HttpContext.Session.GetString(SessionKeys.LoginTime) ?? string.Empty;
        }

        /// <summary>
        /// 加载指定语言的完整资源字典（提供给 View 以便前端查找本地化文本）
        /// </summary>
        protected virtual Dictionary<string, Dictionary<string, string>>? LoadLanguageResources(string languageCode)
        {
            if (_languageService == null) return null;
            try
            {
                var resources = _languageService.GetLanguageResources(languageCode);
                if (resources == null || resources.Count == 0) return null;
                return resources;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载语言资源失败: {Language}", languageCode);
                return null;
            }
        }

        /// <summary>
        /// 虚拟方法：供各业务子控制器返回自定义硬编码文本（无语言资源可用时调用）
        /// </summary>
        protected virtual string GetDefaultText(string section, string key, string cultureCode)
        {
            if (_languageService != null &&
                _languageService.TryGetString(cultureCode, section, key, out var text) &&
                !string.IsNullOrEmpty(text))
            {
                return text;
            }
            return GetDefaultLocalizedFallback(cultureCode);
        }
        #endregion

        #region 结构化日志辅助
        /// <summary>
        /// 获取当前请求的 TraceId（用于结构化日志关联，方便分布式追踪）
        /// </summary>
        protected virtual string GetTraceId()
        {
            return HttpContext?.TraceIdentifier
                ?? System.Diagnostics.Activity.Current?.Id
                ?? Guid.NewGuid().ToString("N");
        }
        #endregion
    }
    #endregion
}
