using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>
    /// 采购收货控制器（Receivin）
    /// 职责：采购订单收货页面，处理 Epicor ReceiptSvc 业务流程
    /// 
    /// 业务流程：
    ///   1. GetNewRcvHead - 初始化收货头
    ///   2. ValidatePONum - 验证采购订单
    ///   3. GetPOInfo - 获取采购订单详情
    ///   4. CNPreUpdate - 本地化预更新
    ///   5. UpdateMaster - 保存收货头
    ///   6. GetNewRcvDtl - 初始化收货明细
    ///   7. GetDtlPOLineInfo - 获取采购行详情
    ///   8. GetDtlQtyInfo - 更新收货数量
    /// </summary>
    // 页面权限用特性声明
    [RequirePermission(MenuCodes.Receivin)]
    public partial class ReceivinController : BaseController
    {
        #region 依赖注入
        private readonly IBusinessLockProvider _lockProvider;
        /// <summary>每日单据操作明细日志服务</summary>
        private readonly DocumentOperationLogService _documentOperationLogger;

        // 锁键 / 幂等键前缀（与其他业务线在共享锁字典中相互隔离）
        private const string LockPrefix = "POReceipt";
        private const string CacheKeyPrefix = "POReceiptRequest:";

        public ReceivinController(
            ILogger<ReceivinController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IMemoryCache memoryCache,
            Services.ILanguageService languageService,
            Services.UserMenuPermissionService permissionService,
            IBusinessLockProvider lockProvider,
            DocumentOperationLogService documentOperationLogger)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
            _documentOperationLogger = documentOperationLogger ?? throw new ArgumentNullException(nameof(documentOperationLogger));
        }
        #endregion

        #region 页面入口 - GET
        [HttpGet]
        public IActionResult Index()
        {
            if (!IsUserAuthenticated())
            {
                _logger.LogWarning("未登录用户尝试访问采购收货页面，重定向到登录页");
                return RedirectToAction("Login", "Account");
            }

            // 页面权限改由类级 [RequirePermission(MenuCodes.Receivin)] 特性统一声明（见类声明处）

            var cultureName = Request.Cookies["Language"] ?? CultureInfo.CurrentUICulture.Name;

            ViewBag.GetText = new Func<string, string, string>((section, key) => GetText(section, key, cultureName));
            ViewBag.CurrentCulture = cultureName;
            ViewBag.UserId = HttpContext.Session.GetString(SessionKeys.UserId) ?? string.Empty;
            ViewBag.UserRole = HttpContext.Session.GetString(SessionKeys.UserRole) ?? string.Empty;
            ViewBag.SiteName = EpicorSessionHelper.GetSiteCode(HttpContext.Session);
            ViewBag.LoginTime = HttpContext.Session.GetString(SessionKeys.LoginTime) ?? string.Empty;

            return View();
        }
        #endregion
    }
}