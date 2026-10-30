﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿// Controllers/UserMenuPermissionController.cs
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    // 用户菜单权限管理属于"系统设置"菜单
    [RequirePermission(MenuCodes.Settings)]
    // 继承 BaseController，统一复用基类的 _logger / _environment /
    // _permissionService / LoadLanguageResources / GetText / GetDefaultText / SetupViewBagLocalization。
    public class UserMenuPermissionController : BaseController
    {
        private readonly IMenuService _menuService;

        // 写操作并发锁前缀；权限配置为单文件全量重写，按用户/菜单键加锁无法阻止
        //     不同权限并发重写同一 JSON 文件 → 资源级全局锁
        private const string LockPrefix = "UserMenuPermission";
        private const string LockKey = "PermissionConfig";

        private readonly IBusinessLockProvider _lockProvider;

        // 说明：logger 仍声明为 ILogger<UserMenuPermissionController>（ILogger<T> 派生自 ILogger），
        // 传入基类后日志类别保持为 UserMenuPermissionController 不变。
        public UserMenuPermissionController(
            ILogger<UserMenuPermissionController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IMemoryCache memoryCache,
            IMenuService menuService,
            UserMenuPermissionService permissionService,
            ILanguageService languageService,
            IBusinessLockProvider lockProvider)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache,
                   languageService, permissionService)
        {
            _menuService = menuService;
            _lockProvider = lockProvider;
        }

        /// <summary>
        /// 用户组下拉选项：取现有权限配置中实际出现过的组名。
        /// 原 UserMenuPermissionService.GetUserGroups() 的硬编码列表（AdminGroup/OperatorGroup）
        /// 与配置中的组名不一致且角色级授权已移除，故改为从配置派生。
        /// </summary>
        private List<string> GetConfiguredUserGroups()
            => _permissionService.GetAllPermissions()
                .Select(p => p.UserGroup)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;

        /// <summary>
        /// 站点下拉选项：全部启用站点 + "全部站点"（空值，兼容遗留全局记录）。
        /// 权限按站点隔离，新建/编辑必须明确站点维度，防止跨站点同名用户互相越权。
        /// </summary>
        private List<SelectListItem> GetSiteOptions()
        {
            var options = FactoryConfigService.GetFactoryOptions()
                .Select(f => new SelectListItem { Value = f.Code, Text = f.Name })
                .ToList();
            options.Insert(0, new SelectListItem
            {
                Value = string.Empty,
                Text = GetText("UserMenuPermissionCreate", "AllSites")
            });
            return options;
        }

        // GET: UserMenuPermission
        public async Task<IActionResult> Index(string searchTerm = "")
        {
            try
            {
                List<UserMenuPermission> permissions;

                if (string.IsNullOrWhiteSpace(searchTerm))
                {
                    permissions = _permissionService.GetAllPermissions();
                }
                else
                {
                    permissions = _permissionService.SearchPermissions(searchTerm);
                }

                ViewBag.SearchTerm = searchTerm;
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                // 设置页面标题 - 使用UserMenuPermissionIndex部分
                ViewData["Title"] = GetText("UserMenuPermissionIndex", "Title");

                return View(permissions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载权限列表时发生错误");
                TempData["ErrorMessage"] = "加载数据失败";
                return View(new List<UserMenuPermission>());
            }
        }

        // GET: UserMenuPermission/Create
        public async Task<IActionResult> Create()
        {
            try
            {
                // 设置用户组
                ViewBag.UserGroups = GetConfiguredUserGroups();
                // 设置站点选项（权限按站点隔离）
                ViewBag.SiteOptions = GetSiteOptions();

                // 设置菜单选项
                ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();

                // 传递本地化函数
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                // 设置页面标题 - 使用UserMenuPermissionCreate部分
                ViewData["Title"] = GetText("UserMenuPermissionCreate", "CreatePermission");

                return View(new UserMenuPermission());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载创建权限页面时发生错误");
                TempData["ErrorMessage"] = "加载页面失败";
                return RedirectToAction(nameof(Index));
            }
        }
        // POST: UserMenuPermission/CreatePost
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreatePost()
        {
            try
            {
                // 从Request.Form读取数据：缺失字段得到空串，交由下方必填校验统一拦截（不用 ! 掩盖 null）
                var permission = new UserMenuPermission
                {
                    SiteCode = Request.Form["SiteCode"].ToString().Trim(),
                    UserCode = Request.Form["UserCode"].ToString(),
                    UserName = Request.Form["UserName"].ToString(),
                    UserGroup = Request.Form["UserGroup"].ToString(),
                    MenuCode = Request.Form["MenuCode"].ToString(),
                    MenuName = Request.Form["MenuName"].ToString(),
                    Remark = Request.Form["Remark"].ToString()
                };

                // 验证必填字段 - 使用UserMenuPermissionCreate部分的文本
                var validationErrors = new List<string>();

                // 站点必须真实存在：空值 = 全部站点（兼容遗留全局记录），
                // 非空则必须是启用站点代码，防止拼错站点造成永不匹配的"僵尸权限"。
                if (!string.IsNullOrEmpty(permission.SiteCode)
                    && FactoryConfigService.GetFactoryOptions().All(f =>
                        !string.Equals(f.Code, permission.SiteCode, StringComparison.OrdinalIgnoreCase)))
                {
                    validationErrors.Add(GetText("UserMenuPermissionCreate", "SiteInvalid"));
                    ModelState.AddModelError("SiteCode", GetText("UserMenuPermissionCreate", "SiteInvalid"));
                }

                // Permission 解析失败必须拒绝：模型默认值 1=允许，缺字段/非法值沿用默认值会造成越权
                if (!int.TryParse(Request.Form["Permission"].ToString(), out int permissionValue))
                {
                    validationErrors.Add(GetText("UserMenuPermissionCreate", "PermissionInvalid"));
                    ModelState.AddModelError("Permission", GetText("UserMenuPermissionCreate", "PermissionInvalid"));
                }
                else
                {
                    permission.Permission = permissionValue;
                }

                _logger.LogInformation(
                    "读取的表单数据: UserCode={UserCode}, UserName={UserName}, UserGroup={UserGroup}, MenuCode={MenuCode}, MenuName={MenuName}, Permission={Permission}, Remark={Remark}",
                    permission.UserCode, permission.UserName, permission.UserGroup,
                    permission.MenuCode, permission.MenuName, permission.Permission, permission.Remark);

                if (string.IsNullOrWhiteSpace(permission.UserCode))
                {
                    validationErrors.Add(GetText("UserMenuPermissionCreate", "UserCodeRequired"));
                    ModelState.AddModelError("UserCode", GetText("UserMenuPermissionCreate", "UserCodeRequired"));
                }

                if (string.IsNullOrWhiteSpace(permission.UserName))
                {
                    validationErrors.Add(GetText("UserMenuPermissionCreate", "UserNameRequired"));
                    ModelState.AddModelError("UserName", GetText("UserMenuPermissionCreate", "UserNameRequired"));
                }

                if (string.IsNullOrWhiteSpace(permission.UserGroup))
                {
                    validationErrors.Add(GetText("UserMenuPermissionCreate", "UserGroupRequired"));
                    ModelState.AddModelError("UserGroup", GetText("UserMenuPermissionCreate", "UserGroupRequired"));
                }

                if (string.IsNullOrWhiteSpace(permission.MenuCode))
                {
                    validationErrors.Add(GetText("UserMenuPermissionCreate", "MenuCodeRequired"));
                    ModelState.AddModelError("MenuCode", GetText("UserMenuPermissionCreate", "MenuCodeRequired"));
                }

                if (string.IsNullOrWhiteSpace(permission.MenuName))
                {
                    validationErrors.Add(GetText("UserMenuPermissionCreate", "MenuNameRequired"));
                    ModelState.AddModelError("MenuName", GetText("UserMenuPermissionCreate", "MenuNameRequired"));
                }

                if (validationErrors.Any())
                {
                    _logger.LogWarning("验证失败: {Errors}", string.Join(", ", validationErrors));
                    TempData["ErrorMessage"] = string.Join("<br/>", validationErrors);
                    ViewBag.UserGroups = GetConfiguredUserGroups();
                    ViewBag.SiteOptions = GetSiteOptions();
                    ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();

                    // 传递本地化函数
                    // 统一设置 ViewBag 本地化/用户信息（基类实现）
                    SetupViewBagLocalization();

                    return View("Create", permission);
                }

                // ====================== 【写操作并发锁】 ======================
                // 权限配置为单文件全量重写：重复判断（读）+ AddPermission（写）必须整体处于锁内，
                // 否则两个并发创建同一权限都会通过重复校验，产生重复记录。
                string traceId = _lockProvider.GetTraceId(HttpContext);
                SemaphoreSlim? permLock = await _lockProvider.TryAcquireLockAsync(
                    LockPrefix, LockKey, traceId, GetCurrentUserId());
                if (permLock == null)
                {
                    TempData["ErrorMessage"] = GetText("UserMenuPermissionCreate", "SystemBusy");
                    ViewBag.UserGroups = GetConfiguredUserGroups();
                    ViewBag.SiteOptions = GetSiteOptions();
                    ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();
                    // 统一设置 ViewBag 本地化/用户信息（基类实现）
                    SetupViewBagLocalization();

                    return View("Create", permission);
                }
                try
                {
                    // ====================== 原子性重复判断 + 强制阻止保存 ======================
                    // 显式处理 null：string.Equals(null, null) 为 true，若两侧都缺失会被误判为"重复"。
                    // 缺字段已在上方必填校验拦截，这里再做一次防空保护。
                    // 唯一键 = 站点 + 用户 + 菜单：同一用户在不同站点可各有授权。
                    bool exists = !string.IsNullOrEmpty(permission.UserCode)
                        && !string.IsNullOrEmpty(permission.MenuCode)
                        && _permissionService.GetAllPermissions().Any(p =>
                            string.Equals(p.SiteCode, permission.SiteCode, StringComparison.Ordinal)
                            && string.Equals(p.UserCode, permission.UserCode, StringComparison.Ordinal)
                            && string.Equals(p.MenuCode, permission.MenuCode, StringComparison.Ordinal));

                    if (exists)
                    {
                        _logger.LogWarning("检测到重复权限：站点 {SiteCode} 用户 {UserCode} 菜单 {MenuCode} 已存在",
                            permission.SiteCode, permission.UserCode, permission.MenuCode);

                        // 强制返回错误，不执行保存
                        TempData["ErrorMessage"] = GetText("UserMenuPermissionIndex", "DuplicatePermission");

                        ViewBag.UserGroups = GetConfiguredUserGroups();
                        ViewBag.SiteOptions = GetSiteOptions();
                        ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();
                        // 统一设置 ViewBag 本地化/用户信息（基类实现）
                        SetupViewBagLocalization();

                        return View("Create", permission);
                    }
                    // ====================================================================================

                    // 添加权限
                    if (_permissionService.AddPermission(permission))
                    {
                        _logger.LogInformation("权限保存成功");
                        TempData["SuccessMessage"] = GetText("UserMenuPermissionCreate", "SaveSuccess");
                        return RedirectToAction(nameof(Index));
                    }
                    else
                    {
                        _logger.LogWarning("权限保存失败");
                        TempData["ErrorMessage"] = GetText("UserMenuPermissionCreate", "SaveFailed");
                    }
                }
                finally
                {
                    _lockProvider.ReleaseLock(permLock, LockPrefix, LockKey, traceId);
                }

                ViewBag.UserGroups = GetConfiguredUserGroups();
                ViewBag.SiteOptions = GetSiteOptions();
                ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();

                // 传递本地化函数
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View("Create", permission);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建权限时发生错误");
                // 不透传 ex.Message（文件/序列化异常原文含服务器路径等内部信息）
                TempData["ErrorMessage"] = GetText("UserMenuPermissionCreate", "SaveFailed");
                ViewBag.UserGroups = GetConfiguredUserGroups();
                ViewBag.SiteOptions = GetSiteOptions();
                ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();

                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View("Create", new UserMenuPermission());
            }
        }


        // GET: UserMenuPermission/Edit
        public async Task<IActionResult> Edit(string userCode, string menuCode, string siteCode = "")
        {
            try
            {
                if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(menuCode))
                {
                    TempData["ErrorMessage"] = GetText("UserMenuPermissionIndex", "EditFailed");
                    return RedirectToAction(nameof(Index));
                }

                var permissions = _permissionService.GetAllPermissions();
                var permission = permissions.FirstOrDefault(p =>
                    string.Equals(p.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase)
                    && p.UserCode == userCode && p.MenuCode == menuCode);

                if (permission == null)
                {
                    TempData["ErrorMessage"] = GetText("UserMenuPermissionIndex", "EditFailed");
                    return RedirectToAction(nameof(Index));
                }

                ViewBag.UserGroups = GetConfiguredUserGroups();
                ViewBag.SiteOptions = GetSiteOptions();
                ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();
                ViewBag.OriginalUserCode = userCode;
                ViewBag.OriginalMenuCode = menuCode;
                ViewBag.OriginalSiteCode = siteCode ?? string.Empty;

                // 传递本地化函数
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                // 设置页面标题 - 使用UserMenuPermissionCreate部分
                ViewData["Title"] = GetText("UserMenuPermissionCreate", "EditPermission");

                return View(permission);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载编辑页面时发生错误");
                // 使用默认文本
                TempData["ErrorMessage"] = GetDefaultText("UserMenuPermissionIndex", "EditFailed", "zh-CN");
                return RedirectToAction(nameof(Index));
            }
        }

        // POST: UserMenuPermission/Edit
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditPost(string originalUserCode, string originalMenuCode, string originalSiteCode = "")
        {
            try
            {
                var permission = new UserMenuPermission
                {
                    SiteCode = Request.Form["SiteCode"].ToString().Trim(),
                    UserCode = Request.Form["UserCode"]!,
                    UserName = Request.Form["UserName"]!,
                    UserGroup = Request.Form["UserGroup"]!,
                    MenuCode = Request.Form["MenuCode"]!,
                    MenuName = Request.Form["MenuName"]!,
                    Remark = Request.Form["Remark"]!
                };

                // Permission 解析失败必须拒绝（与 CreatePost 同口径）：
                // 模型默认值 1=允许，缺字段/非法值时沿用默认值会把"禁止"静默改成"允许"，属越权。
                if (!int.TryParse(Request.Form["Permission"].ToString(), out int permissionValue))
                {
                    _logger.LogWarning("编辑权限时 Permission 非法: SiteCode={SiteCode}, UserCode={UserCode}, MenuCode={MenuCode}",
                        originalSiteCode, originalUserCode, originalMenuCode);
                    TempData["ErrorMessage"] = GetText("UserMenuPermissionCreate", "PermissionInvalid");

                    // 回显库中已持久化的记录，避免把默认值 1（允许）当作用户输入展示
                    var persisted = _permissionService.GetAllPermissions().FirstOrDefault(p =>
                        string.Equals(p.SiteCode, originalSiteCode, StringComparison.OrdinalIgnoreCase)
                        && p.UserCode == originalUserCode && p.MenuCode == originalMenuCode) ?? permission;

                    ViewBag.UserGroups = GetConfiguredUserGroups();
                    ViewBag.SiteOptions = GetSiteOptions();
                    ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();
                    ViewBag.OriginalUserCode = originalUserCode;
                    ViewBag.OriginalMenuCode = originalMenuCode;
                    ViewBag.OriginalSiteCode = originalSiteCode ?? string.Empty;

                    // 统一设置 ViewBag 本地化/用户信息（基类实现）
                    SetupViewBagLocalization();

                    return View("Edit", persisted);
                }
                permission.Permission = permissionValue;

                // 更新权限（单文件全量重写）取资源级锁串行化
                string traceId = _lockProvider.GetTraceId(HttpContext);
                SemaphoreSlim? permLock = await _lockProvider.TryAcquireLockAsync(
                    LockPrefix, LockKey, traceId, GetCurrentUserId());
                if (permLock == null)
                {
                    TempData["ErrorMessage"] = GetText("UserMenuPermissionCreate", "SystemBusy");
                    ViewBag.UserGroups = GetConfiguredUserGroups();
                    ViewBag.SiteOptions = GetSiteOptions();
                    ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();
                    ViewBag.OriginalUserCode = originalUserCode;
                    ViewBag.OriginalMenuCode = originalMenuCode;
                    ViewBag.OriginalSiteCode = originalSiteCode ?? string.Empty;

                    // 统一设置 ViewBag 本地化/用户信息（基类实现）
                    SetupViewBagLocalization();

                    return View("Edit", permission);
                }
                try
                {
                    if (_permissionService.UpdatePermission(originalUserCode, originalMenuCode,
                            permission, originalSiteCode))
                    {
                        TempData["SuccessMessage"] = GetText("UserMenuPermissionCreate", "UpdateSuccess");
                        return RedirectToAction(nameof(Index));
                    }
                    else
                    {
                        TempData["ErrorMessage"] = GetText("UserMenuPermissionCreate", "UpdateFailed");
                    }
                }
                finally
                {
                    _lockProvider.ReleaseLock(permLock, LockPrefix, LockKey, traceId);
                }

                ViewBag.UserGroups = GetConfiguredUserGroups();
                ViewBag.SiteOptions = GetSiteOptions();
                ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();
                ViewBag.OriginalUserCode = originalUserCode;
                ViewBag.OriginalMenuCode = originalMenuCode;
                ViewBag.OriginalSiteCode = originalSiteCode ?? string.Empty;

                // 传递本地化函数
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View("Edit", permission);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新权限时发生错误");
                // 不透传 ex.Message（内部信息）
                TempData["ErrorMessage"] = GetText("UserMenuPermissionCreate", "UpdateFailed");
                ViewBag.UserGroups = GetConfiguredUserGroups();
                ViewBag.SiteOptions = GetSiteOptions();
                ViewBag.MenuOptions = _menuService != null ? await _menuService.GetMenuSelectItemsAsync() : new List<SelectListItem>();
                ViewBag.OriginalUserCode = originalUserCode;
                ViewBag.OriginalMenuCode = originalMenuCode;
                ViewBag.OriginalSiteCode = originalSiteCode ?? string.Empty;

                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View("Edit", new UserMenuPermission());
            }
        }

        // POST: UserMenuPermission/Delete
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string userCode, string menuCode, string siteCode = "")
        {
            try
            {
                if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(menuCode))
                {
                    TempData["ErrorMessage"] = GetText("UserMenuPermissionIndex", "DeleteFailed");
                    return RedirectToAction(nameof(Index));
                }

                // 删除权限（单文件全量重写）取资源级锁串行化
                string traceId = _lockProvider.GetTraceId(HttpContext);
                SemaphoreSlim? permLock = await _lockProvider.TryAcquireLockAsync(
                    LockPrefix, LockKey, traceId, GetCurrentUserId());
                if (permLock == null)
                {
                    TempData["ErrorMessage"] = GetText("UserMenuPermissionIndex", "SystemBusy");
                    return RedirectToAction(nameof(Index));
                }
                try
                {
                    if (_permissionService.DeletePermission(userCode, menuCode, siteCode))
                    {
                        TempData["SuccessMessage"] = GetText("UserMenuPermissionIndex", "DeleteSuccess");
                    }
                    else
                    {
                        TempData["ErrorMessage"] = GetText("UserMenuPermissionIndex", "DeleteFailed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "删除权限时发生错误");
                    // 不透传 ex.Message（内部信息）
                    TempData["ErrorMessage"] = GetText("UserMenuPermissionIndex", "DeleteFailed");
                }
                finally
                {
                    _lockProvider.ReleaseLock(permLock, LockPrefix, LockKey, traceId);
                }

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                // 外层兜底：锁获取等环节异常时保持原有失败响应（TempData + 重定向）
                _logger.LogError(ex, "删除权限时发生错误");
                TempData["ErrorMessage"] = GetText("UserMenuPermissionIndex", "DeleteFailed");
                return RedirectToAction(nameof(Index));
            }
        }
    }
}