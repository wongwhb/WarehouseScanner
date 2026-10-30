using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    public class HomeController : BaseController
    {
        public HomeController(
            ILogger<HomeController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IMemoryCache memoryCache,
            ILanguageService languageService,
            UserMenuPermissionService permissionService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
        }

        #region 按钮权限
        /// <summary>基于 UserMenuPermissionService 检查指定菜单的权限</summary>
        private bool HasButtonPermission(string menuCode)
        {
            try
            {
                var userId = HttpContext.Session.GetString(SessionKeys.UserId);
                var userRole = HttpContext.Session.GetString(SessionKeys.UserRole) ?? string.Empty;
                if (string.IsNullOrEmpty(userId)) return false;

                // fail-closed：权限服务缺失时不能放行，
                // 否则全部菜单按钮会被判定为"有权限"，属静默放行。
                if (_permissionService == null)
                {
                    _logger.LogError("权限服务未初始化，菜单按钮按失败关闭策略判定为无权限: {MenuCode}", menuCode);
                    return false;
                }

                // 权限按站点隔离：按钮权限同样以当前站点维度判定
                var siteCode = HttpContext.Session.GetString(SessionKeys.SiteCode) ?? string.Empty;
                return _permissionService.HasPermission(userId, userRole, menuCode, siteCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查按钮权限时发生错误: {MenuCode}", menuCode);
                return false;
            }
        }
        #endregion

        #region 主页
        public IActionResult Index()
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问主页，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                var language = Request.Cookies[CookieNames.Language] ?? AppDefaults.DefaultLanguage;
                var culture = new CultureInfo(language);

                // 使用基类提供的语言资源加载方法
                var languageResources = LoadLanguageResources(culture.Name);
                if (languageResources == null || languageResources.Count == 0)
                {
                    _logger.LogError("无法加载语言资源文件: {Lang}.xml，使用英文作为备选", culture.Name);
                    if (culture.Name != "en-US")
                    {
                        languageResources = LoadLanguageResources("en-US");
                        if (languageResources == null || languageResources.Count == 0)
                            throw new InvalidOperationException("语言文件加载失败: zh-CN.xml 和 en-US.xml 均不存在");
                        culture = new CultureInfo("en-US");
                    }
                    else
                    {
                        throw new InvalidOperationException("语言文件加载失败: en-US.xml 不存在");
                    }
                }

                // 构建本地化文本查找委托，供 View 直接使用
                string GetText(string section, string key)
                {
                    if (languageResources != null &&
                        languageResources.TryGetValue(section, out var sectionResources) &&
                        sectionResources.TryGetValue(key, out var value))
                        return value;

                    _logger.LogWarning("未找到文本资源: section={Section}, key={Key}, language={Lang}",
                        section, key, culture.Name);
                    return GetDefaultLocalizedFallback(culture.Name);
                }

                var userId = HttpContext.Session.GetString(SessionKeys.UserId);
                var userRole = HttpContext.Session.GetString(SessionKeys.UserRole);
                var siteName = EpicorSessionHelper.GetSiteCode(HttpContext.Session);
                var loginTime = HttpContext.Session.GetString(SessionKeys.LoginTime);
                var userFullName = HttpContext.Session.GetString(SessionKeys.UserFullName) ?? userId;

                var buttonPermissions = new Dictionary<string, bool>
                {
                    [MenuCodes.Issue] = HasButtonPermission(MenuCodes.Issue),
                    [MenuCodes.ReturnMaterial] = HasButtonPermission(MenuCodes.ReturnMaterial),
                    [MenuCodes.Outbound] = HasButtonPermission(MenuCodes.Outbound),
                    [MenuCodes.Inbound] = HasButtonPermission(MenuCodes.Inbound),
                    [MenuCodes.Receivin] = HasButtonPermission(MenuCodes.Receivin),
                    [MenuCodes.Transfer] = HasButtonPermission(MenuCodes.Transfer),
                    [MenuCodes.Inventory] = HasButtonPermission(MenuCodes.Inventory),
                    [MenuCodes.LabelPrint] = HasButtonPermission(MenuCodes.LabelPrint),
                    [MenuCodes.Settings] = HasButtonPermission(MenuCodes.Settings)
                };

                ViewData[SessionKeys.UserId] = userId;
                ViewData[SessionKeys.UserRole] = userRole;
                ViewData["SiteName"] = siteName;
                ViewData["LoginTime"] = loginTime;
                ViewData[SessionKeys.UserFullName] = userFullName;
                ViewData["LanguageResources"] = languageResources;
                ViewData["ButtonPermissions"] = buttonPermissions;

                ViewBag.GetText = new Func<string, string, string>(GetText);
                ViewBag.CurrentCulture = culture.Name;

                return View();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问主页时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }
        #endregion

        #region 系统设置页
        // 系统设置入口页指向的 4 个目标页（Config / Factory / Language / UserMenuPermission）
        //   均要求 MenuCodes.Settings，本页须保持一致的权限要求。
        [RequirePermission(MenuCodes.Settings)]
        public IActionResult SystemSett()
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问系统设置，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                var language = Request.Cookies[CookieNames.Language] ?? AppDefaults.DefaultLanguage;
                var culture = new CultureInfo(language);

                var languageResources = LoadLanguageResources(culture.Name);
                if (languageResources == null || languageResources.Count == 0)
                {
                    _logger.LogError("无法加载语言资源文件: {Lang}.xml，使用英文作为备选", culture.Name);
                    if (culture.Name != "en-US")
                    {
                        languageResources = LoadLanguageResources("en-US");
                        if (languageResources == null || languageResources.Count == 0)
                            throw new InvalidOperationException("语言文件加载失败: zh-CN.xml 和 en-US.xml 均不存在");
                        culture = new CultureInfo("en-US");
                    }
                    else
                    {
                        throw new InvalidOperationException("语言文件加载失败: en-US.xml 不存在");
                    }
                }

                string GetText(string section, string key)
                {
                    if (languageResources != null &&
                        languageResources.TryGetValue(section, out var sectionResources) &&
                        sectionResources.TryGetValue(key, out var value))
                        return value;

                    _logger.LogWarning("未找到文本资源: section={Section}, key={Key}, language={Lang}",
                        section, key, culture.Name);
                    return GetDefaultLocalizedFallback(culture.Name);
                }

                var userId = HttpContext.Session.GetString(SessionKeys.UserId);
                var userFullName = HttpContext.Session.GetString(SessionKeys.UserFullName);
                var userRole = HttpContext.Session.GetString(SessionKeys.UserRole);
                var siteName = EpicorSessionHelper.GetSiteCode(HttpContext.Session);
                var loginTime = HttpContext.Session.GetString(SessionKeys.LoginTime);

                ViewData[SessionKeys.UserId] = userId;
                ViewData[SessionKeys.UserFullName] = userFullName;
                ViewData[SessionKeys.UserRole] = userRole;
                ViewData["SiteName"] = siteName;
                ViewData["LoginTime"] = loginTime;
                ViewData["LanguageResources"] = languageResources;

                ViewBag.GetText = new Func<string, string, string>(GetText);
                ViewBag.CurrentCulture = culture.Name;

                return View();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问系统设置页面时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }
        #endregion

        #region 错误页
        /// <summary>
        /// 通用错误页：仅展示友好文案，不携带/展示任何异常详情，
        /// 异常细节由全局异常中间件按 TraceIdentifier 记录到日志。
        /// </summary>
        [AllowAnonymous]
        public IActionResult Error()
        {
            var languageResources = LoadLanguageResources(GetCurrentLanguage());
            ViewData["LanguageResources"] = languageResources;
            SetupViewBagLocalization(languageResources);
            return View();
        }
        #endregion
    }
}
