using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using WarehouseScanner.Services;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    #region 账户控制器
    /// <summary>
    /// 账户控制器。
    /// CookieNames/SessionKeys/EpicorSessionHelper 使用 Utilities 命名空间下的统一版本；
    /// 通过 ILanguageService 读取本地化文本。
    /// 继承 BaseController，复用基类的 _logger / _protector /
    /// _epicorServiceFactory / _languageService / IsUserAuthenticated / GetCurrentUserId。
    /// </summary>
    public class AccountController : BaseController
    {
        private readonly FactoryConfigService _factoryService;
        private readonly IConfiguration _configuration;
        private readonly IAntiforgery _antiforgery;

        // 说明：logger 仍声明为 ILogger<AccountController>（ILogger<T> 派生自 ILogger），
        // 传入基类后日志类别保持为 AccountController 不变。
        // _protector 由基类以同一用途串（"AccountController.SensitiveData"）创建，语义完全一致。
        public AccountController(
            FactoryConfigService factoryService,
            ILogger<AccountController> logger,
            IWebHostEnvironment environment,
            IConfiguration configuration,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IAntiforgery antiforgery,
            IMemoryCache memoryCache,
            ILanguageService languageService,
            UserMenuPermissionService permissionService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache,
                   languageService, permissionService)
        {
            _factoryService = factoryService ?? throw new ArgumentNullException(nameof(factoryService));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _antiforgery = antiforgery ?? throw new ArgumentNullException(nameof(antiforgery));
        }

        #region 登录页面 (GET)
        [HttpGet]
        public async Task<IActionResult> Login(string lang = "", string logout = "", string error = "")
        {
            // 禁用缓存：防止浏览器后退时加载带旧 Token 的缓存页面
            // 旧 Token 在登录成功后已被消费，缓存页面提交时会因 Token 校验失败而返回 400
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            if (!string.IsNullOrEmpty(logout) && logout == "true")
            {
                string userId = GetCurrentUserId();
                string apiUrl = HttpContext.Session.GetString(SessionKeys.ApiUrl) ?? string.Empty;
                var target = new EpicorSiteTarget(
                    EpicorSessionHelper.GetSiteCode(HttpContext.Session),
                    EpicorSessionHelper.GetEpicorCompany(HttpContext.Session),
                    EpicorSessionHelper.GetPlant(HttpContext.Session));

                if (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(apiUrl))
                    await _epicorServiceFactory.RemoveAsync(userId, apiUrl, target);

                HttpContext.Session.Clear();

                // 说明：无 Cookie 认证管线，HttpContext.User 不由任何认证方案填充，
                // 防伪令牌始终绑定匿名身份，故无需再手动重置用户身份。

                // Delete antiforgery cookie to force a fresh token on next page render
                DeleteAntiforgeryCookie();
                ViewData["LogoutMessage"] = GetLocalizedMessage("LogoutSuccess", lang) ?? "您已成功退出系统";
            }

            // 从 query string 读取异常中间件传递的错误信息（如 AntiForgery Token 失效）
            if (!string.IsNullOrEmpty(error))
            {
                ViewData["ErrorMessage"] = error;
            }

            string currentLanguage = ValidateAndNormalizeLanguage(lang) ?? AppDefaults.DefaultLanguage;
            ViewData["CurrentLanguage"] = currentLanguage;
            ViewData["SupportedLanguages"] = _languageService!.GetSupportedLanguages();
            ViewData["LanguageResources"] = _languageService!.GetLanguageResources(currentLanguage);
            // 登录页只需要站点代码/名称：使用轻量投影，避免 GetFactories 触发 CloneDecrypt
            // 把各站点明文 ApiKey 复制进 ViewData。
            ViewData["Factories"] = _factoryService.GetFactoryOptions(currentLanguage);
            ViewData["Username"] = TempData["LastUsername"] as string ?? string.Empty;

            return View();
        }
        #endregion

        #region 登录处理 (POST)
        [HttpPost]
        public async Task<IActionResult> LoginAsync(
            string username, string password, string language, string site, string? deviceName = null)
        {
            // 手动验证 AntiForgery Token（替代 [ValidateAntiForgeryToken] Filter）
            // 原因：Filter 在校验失败时设置 StatusCode=400 且不抛异常，
            // 导致全局异常中间件无法捕获并重定向，用户看到 400 错误页。
            //
            // Token 校验失败必须一律拒绝登录，不能因为"请求里带了用户名/密码"就放行。
            //       登录 CSRF 缺口必须防御：攻击者可构造跨站表单，在受害者浏览器上
            //       用攻击者自己的账号密码完成登录，此后受害者录入的业务数据会落到攻击者账号下。
            //       "浏览器后退导致 Token 过期"的体验问题已在本方法开头用
            //       Cache-Control: no-store 解决（回退不会命中带旧 Token 的缓存页面）；
            //       这里保留用户名回填（TempData["LastUsername"]），让用户只需重新提交。
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (AntiforgeryValidationException ex)
            {
                _logger.LogWarning(ex,
                    "AntiForgery Token 校验失败，拒绝本次登录请求并重定向到登录页。Username: {Username}",
                    username);
                TempData["ErrorMessage"] = "登录状态已变更，请重新登录";
                TempData["LastUsername"] = username;
                return RedirectToAction("Login", new { lang = language });
            }

            bool hasCredentials = !string.IsNullOrWhiteSpace(username)
                                && !string.IsNullOrWhiteSpace(password)
                                && !string.IsNullOrWhiteSpace(site);

            if (!hasCredentials)
            {
                TempData["ErrorMessage"] = GetLocalizedMessage("LoginRequired", language) ?? "请输入用户名、密码和站点";
                TempData["LastUsername"] = username;
                return RedirectToAction("Login", new { lang = language });
            }

            try
            {
                var factory = _factoryService.GetFactory(site, language);
                if (factory == null)
                {
                    TempData["ErrorMessage"] = GetLocalizedMessage("SiteInvalid", language) ?? "所选站点无效";
                    TempData["LastUsername"] = username;
                    return RedirectToAction("Login", new { lang = language });
                }

                if (string.IsNullOrWhiteSpace(factory.ApiUrl) || string.IsNullOrWhiteSpace(factory.ApiKey))
                {
                    TempData["ErrorMessage"] = GetLocalizedMessage("SiteConfigIncomplete", language) ?? "站点配置不完整，请联系管理员";
                    TempData["LastUsername"] = username;
                    return RedirectToAction("Login", new { lang = language });
                }

                string firstName = string.Empty;
                string lastName = string.Empty;
                string userFullName = string.Empty;
                string character02 = string.Empty;

                // Epicor 调用上下文：站点代码 + 生效公司 + 工厂
                var target = new EpicorSiteTarget(factory.Code, factory.EffectiveCompany, factory.Plant);

                var epicorRest = _epicorServiceFactory.CreateLoginSession(username, password, factory.ApiUrl, factory.ApiKey, target);
                try
                {
                    bool tokenOk = await epicorRest.GetEpicorToken();
                    if (!tokenOk)
                    {
                        // 优先显示 Epicor API 返回的详细错误信息（如 401 Unauthorized + TraceId）
                        string apiError = epicorRest.HasApiError ? epicorRest.LastApiError : string.Empty;
                        string defaultMessage = GetLocalizedMessage("InvalidCredentials", language) ?? "用户名或密码错误";
                        string errorMessage = string.IsNullOrEmpty(apiError) ? defaultMessage : $"{defaultMessage}（{apiError}）";

                        _logger.LogWarning("用户登录 Token 获取失败: {Username}, Site: {Site}, Error: {ApiError}",
                            username, site, apiError);

                        TempData["ErrorMessage"] = errorMessage;
                        TempData["LastUsername"] = username;
                        return RedirectToAction("Login", new { lang = language });
                    }

                    var empResult = await epicorRest.EmpBasicGetByID(username);
                    firstName = empResult.firstName;
                    lastName = empResult.lastName;
                    userFullName = string.IsNullOrEmpty(firstName) ? username : $"{firstName}{lastName}".Trim();
                    character02 = empResult.character02;

                    if (character02 == "ERROR" || character02 == "FAIL"
                        || (string.IsNullOrEmpty(firstName) && string.IsNullOrEmpty(lastName) && !string.IsNullOrEmpty(character02)))
                    {
                        // 获取员工信息失败时，也检查 API 错误
                        string empApiError = epicorRest.HasApiError ? epicorRest.LastApiError : string.Empty;
                        string defaultMsg = GetLocalizedMessage("EpicorConnectionLimit", language) ?? "Epicor已达到最大连接限制，获取员工信息失败";
                        string empErrorMessage = string.IsNullOrEmpty(empApiError) ? defaultMsg : $"{defaultMsg}（{empApiError}）";

                        _logger.LogWarning("用户登录获取员工信息失败: {Username}, Site: {Site}, ApiError: {ApiError}",
                            username, site, empApiError);

                        TempData["ErrorMessage"] = empErrorMessage;
                        TempData["LastUsername"] = username;
                        return RedirectToAction("Login", new { lang = language });
                    }
                }
                finally
                {
                    // 登录校验完成：释放校验专用 Epicor 会话，避免残留 License（成功/失败/异常均执行）
                    try { await epicorRest.LogoutAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "登录校验后 Logout 尽力而为失败"); }
                    epicorRest.Dispose();
                }

                // 清空旧 Session 数据，防止残留凭据混入（Session.Clear 已清除所有键值）
                // 注意：不能调用 Response.Cookies.Delete(Session)，否则 SessionMiddleware
                // 不会重新下发 cookie（仅 _isNewSession=true 时才下发），导致后续请求丢失 Session
                HttpContext.Session.Clear();

                // 写入 Session（密码/API Key 加密；站点三元组原子写入）
                EpicorSessionHelper.SetCredentials(HttpContext.Session, username, password,
                    factory.ApiUrl, factory.ApiKey, target, _protector);

                HttpContext.Session.SetString(SessionKeys.UserId, username);
                HttpContext.Session.SetString(SessionKeys.UserFullName, userFullName);
                // 登录时间：主页/系统设置页展示用（原键从未写入，展示恒为空）
                HttpContext.Session.SetString(SessionKeys.LoginTime, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                CreateUserAuthCookies(site);

                // 持久化用户在登录页选择的语言，确保登录后各页面语言一致
                var normalizedLanguage = ValidateAndNormalizeLanguage(language) ?? AppDefaults.DefaultLanguage;
                Response.Cookies.Append(CookieNames.Language, normalizedLanguage, new CookieOptions
                {
                    Expires = DateTimeOffset.Now.AddYears(1),
                    Path = "/",
                    HttpOnly = false,
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = Request.IsHttps
                });

                RecordLoginLog(username, site, deviceName);
                _logger.LogInformation("用户登录成功: {Username}, 站点: {Site}, IP: {IpAddress}",
                    username, site, GetClientIpAddress());

                return RedirectToAction("Index", "Home");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户登录过程中发生错误: {Username}", username);
                TempData["ErrorMessage"] = GetLocalizedMessage("SystemError", language) ?? "系统错误，请稍后再试";
                TempData["LastUsername"] = username;
                return RedirectToAction("Login", new { lang = language });
            }
        }
        #endregion

        #region 登出 (POST)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            string userId = GetCurrentUserId();
            string apiUrl = HttpContext.Session.GetString(SessionKeys.ApiUrl) ?? string.Empty;
            var target = new EpicorSiteTarget(
                EpicorSessionHelper.GetSiteCode(HttpContext.Session),
                EpicorSessionHelper.GetEpicorCompany(HttpContext.Session),
                EpicorSessionHelper.GetPlant(HttpContext.Session));

            _logger.LogInformation("用户登出: {UserId}", userId);

            try
            {
                if (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(apiUrl))
                    await _epicorServiceFactory.RemoveAsync(userId, apiUrl, target);

                HttpContext.Session.Clear();
                // 注意：不删除 Language Cookie，保持用户上次选择的语言偏好，
                // 避免重新登录后界面语言显示不一致（部分文本回退为英文）。

                // Delete antiforgery cookie to ensure fresh token on next login page
                DeleteAntiforgeryCookie();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户登出过程中发生错误: {UserId}", userId);
            }

            // 清理已在本 POST 内完成（RemoveAsync + Session.Clear + 防伪 Cookie），
            // 重定向时不携带 logout=true，避免 GET Login 分支重复执行一遍 RemoveAsync。
            return RedirectToAction("Login");
        }
        #endregion

        #region 切换语言
        [HttpGet]
        public IActionResult ChangeLanguage(string language)
        {
            var normalized = ValidateAndNormalizeLanguage(language) ?? AppDefaults.DefaultLanguage;

            Response.Cookies.Append(CookieNames.Language, normalized, new CookieOptions
            {
                Expires = DateTimeOffset.Now.AddYears(1),
                Path = "/",
                HttpOnly = false,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps
            });

            return RedirectToAction("Login", new { lang = normalized });
        }
        #endregion

        #region 会话状态检查
        [HttpGet]
        [AllowAnonymous]
        public IActionResult CheckSession()
        {
            try
            {
                bool isAuthenticated = IsUserAuthenticated();
                return Json(new
                {
                    isAuthenticated,
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "检查会话状态时发生异常");
                return Json(new { isAuthenticated = false });
            }
        }
        #endregion

        #region 清除会话
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearSession()
        {
            try
            {
                string userId = GetCurrentUserId();
                string apiUrl = HttpContext.Session.GetString(SessionKeys.ApiUrl) ?? string.Empty;
                var target = new EpicorSiteTarget(
                    EpicorSessionHelper.GetSiteCode(HttpContext.Session),
                    EpicorSessionHelper.GetEpicorCompany(HttpContext.Session),
                    EpicorSessionHelper.GetPlant(HttpContext.Session));

                _logger.LogInformation("手动清除会话，用户: {UserId}", userId);

                if (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(apiUrl))
                    await _epicorServiceFactory.RemoveAsync(userId, apiUrl, target);

                HttpContext.Session.Clear();

                return Json(new { success = true, message = GetLocalText("Login", "AuthCleared") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清除认证时发生错误");
                return Json(new { success = false, message = GetLocalText("Login", "AuthClearFailed") });
            }
        }
        #endregion

        #region 私有辅助方法
        private string? ValidateAndNormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return null;
            language = language.Trim().ToLowerInvariant();
            return language switch
            {
                "zh-cn" or "zh" => "zh-CN",
                "en-us" or "en" => "en-US",
                _ => null
            };
        }

        private void CreateUserAuthCookies(string site)
        {
            Response.Cookies.Append(CookieNames.LastSelectedSite, site, new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                Path = "/",
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps
            });
        }

        private void DeleteAntiforgeryCookie()
        {
            try
            {
                // 真实 Cookie 名以点开头（.AspNetCore.Antiforgery.*），
                // 同时兼容无前导点的历史/变体命名。
                var prefixes = new[] { ".AspNetCore.Antiforgery.", "AspNetCore.Antiforgery." };
                foreach (var cookie in Request.Cookies.Keys)
                {
                    if (prefixes.Any(p => cookie.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        Response.Cookies.Delete(cookie);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete antiforgery cookie");
            }
        }

        private string GetLocalizedMessage(string messageKey, string language)
        {
            string[] categoriesToTry = { "Login", "Home", "Common", "Errors" };
            var lang = string.IsNullOrWhiteSpace(language) ? AppDefaults.DefaultLanguage : language;

            foreach (var category in categoriesToTry)
            {
                if (_languageService != null &&
                    _languageService.TryGetString(lang, category, messageKey, out var localized) &&
                    !string.IsNullOrEmpty(localized))
                {
                    return localized;
                }
            }

            // 资源缺失时返回可读兜底文案（不使用 "[section.key]" 占位符）
            if (_languageService != null &&
                _languageService.TryGetString(lang, "Common", "OperationFailedRetry", out var fallback) &&
                !string.IsNullOrEmpty(fallback))
            {
                return fallback;
            }

            return messageKey;
        }

        private string GetClientIpAddress()
        {
            try
            {
                var remoteIp = HttpContext.Connection.RemoteIpAddress;
                if (remoteIp == null) return "Unknown";
                return remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4().ToString() : remoteIp.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取客户端 IP 地址失败");
                return "Unknown";
            }
        }

        private void RecordLoginLog(string username, string site, string? deviceName)
        {
            try
            {
                string clientIp = GetClientIpAddress();
                string userAgent = Request.Headers["User-Agent"].ToString();
                _logger.LogInformation("LoginLog: {Username}, Site: {Site}, IP: {IpAddress}, Device: {DeviceName}, UserAgent: {UserAgent}",
                    username, site, clientIp, deviceName ?? "Unknown", userAgent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "记录登录日志时发生错误: {Username}", username);
            }
        }
        #endregion
    }
    #endregion
}
