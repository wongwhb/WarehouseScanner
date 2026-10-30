using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    // 页面权限用特性声明
    [RequirePermission(MenuCodes.Outbound)]
    public partial class OutboundController : BaseController
    {
        private readonly IConfiguration _configuration;
        private readonly FactoryConfigService _factoryService;
        private readonly IEpicorQueryGateway _queryGateway;
        private readonly IBusinessLockProvider _lockProvider;
        private readonly OutboundBusinessService _outboundBusinessService;
        private readonly ShippingScheduleQueryService _shippingScheduleService;

        // 锁键前缀（与 Return/Transfer/Issue/Receipt 在共享锁字典中相互隔离）
        private const string LockPrefix = "Outbound";

        // Validate* 接口短缓存键前缀 + TTL（30 秒）：
        // 连续扫码场景下，同一张单会触发 N 次 ValidateOrder/ValidateInventory，
        // 不加缓存会把同一 Epicor 查询打爆；加 30 秒短缓存即可，且不影响业务正确性。
        private const string CacheKey_OrderRels = "Outbound:OrderRels:";      // + company|orderNum|orderLine
        private const int    CacheTtl_OrderRelsSeconds = 30;

        // 客户订单查询短缓存：同一用户反复查询同一客户（或切换页签再回来）时，30s 内命中缓存、不调 Epicor。
        private const string CacheKey_SearchOrders = "Outbound:SearchOrders:"; // + company|custID|custName|orderNum|poNum
        private const int    CacheTtl_SearchOrdersSeconds = 30;

        // 用户 PackId 存储说明：实际使用单文件 App_Data/UserPackId.json（见 OutboundController.UserPackId.cs），
        // 由 _packFileLock 保证 read-modify-write 原子性，多用户各写各的 key。

        /// <summary>
        /// 缓存租户标识（当前会话的 公司|工厂 小写键）。
        /// IMemoryCache 为应用级单例、多站点会话共用同一实例，缓存键必须带 公司+工厂 维度，
        /// 否则不同公司/工厂的用户会互相命中同一业务键（订单行 / 客户订单 / 物料库存），
        /// 拿到别家站点的订单行与库存数做校验，产生误导性结论。
        /// </summary>
        private string CacheTenant => CurrentTenantKey;

        public OutboundController(
            FactoryConfigService factoryService,
            ILanguageService languageService,
            ILogger<OutboundController> logger,
            IEpicorQueryGateway queryGateway,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            UserMenuPermissionService permissionService,
            IMemoryCache memoryCache,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IBusinessLockProvider lockProvider,
            OutboundBusinessService outboundBusinessService,
            ShippingScheduleQueryService shippingScheduleService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _factoryService = factoryService ?? throw new ArgumentNullException(nameof(factoryService));
            _queryGateway = queryGateway ?? throw new ArgumentNullException(nameof(queryGateway));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
            _outboundBusinessService = outboundBusinessService ?? throw new ArgumentNullException(nameof(outboundBusinessService));
            _shippingScheduleService = shippingScheduleService ?? throw new ArgumentNullException(nameof(shippingScheduleService));
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问发货页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                // 页面权限改由类级 [RequirePermission(MenuCodes.Outbound)] 特性统一声明（见类声明处）

                CreateEpicorAuthServiceFromSession();

                var language = Request.Cookies["Language"];
                var culture = !string.IsNullOrEmpty(language)
                    ? new CultureInfo(language)
                    : CultureInfo.CurrentUICulture;

                // 统一设置 ViewBag 本地化和用户信息（替代重复的 GetLocalText + ViewBag 赋值）
                SetupViewBagLocalization();

                string currentUserId = HttpContext.Session.GetString(SessionKeys.UserId) ?? string.Empty;
                var lastPack = await ReadUserLastPackIdAsync(currentUserId);
                ViewBag.LastPackId = lastPack;

                return View();
            }
            catch (UnauthorizedAccessException)
            {
                return RedirectToAction("Login", "Account");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问发货页面时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ShipOrder([FromForm] ShipmentRequest model, [FromHeader(Name = "X-Request-ID")] string? requestId = null)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";

            #region 参数校验
            if (string.IsNullOrWhiteSpace(model.SalesOrderNo))
                return Json(new { success = false, message = GetLocalText("Outbound", "SalesOrderRequired") });
            if (string.IsNullOrWhiteSpace(model.MaterialCode))
                return Json(new { success = false, message = GetLocalText("Outbound", "MaterialCodeRequired") });
            if (string.IsNullOrWhiteSpace(model.WarehouseCode))
                return Json(new { success = false, message = GetLocalText("Outbound", "WarehouseCodeRequired") });
            if (string.IsNullOrWhiteSpace(model.Location))
                return Json(new { success = false, message = GetLocalText("Outbound", "LocationRequired") });
            if (model.Quantity <= 0)
                return Json(new { success = false, message = GetLocalText("Outbound", "QuantityRequired") });
            // 订单行号 / 发货批次号必填且必须大于 0（避免 Service 层 ?? 1 兜底发错单）
            if (!model.OrderLine.HasValue || model.OrderLine.Value <= 0)
                return Json(new { success = false, message = GetLocalText("Outbound", "SalesOrderLineRequired") });
            if (!model.OrderRelNum.HasValue || model.OrderRelNum.Value <= 0)
                return Json(new { success = false, message = GetLocalText("Outbound", "OrderRelNumRequired") });
            // 装箱单号必填且必须大于 0（避免默认发往 PackNum=1 的错误装箱单）
            if (!model.packId.HasValue || model.packId.Value <= 0)
                return Json(new { success = false, message = GetLocalText("Outbound", "PackIdRequired") });
            #endregion

            #region 调用业务服务
            string operatorId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";

            var (success, message, data) = await _outboundBusinessService.ShipOrderAsync(
                model, operatorId, requestId);

            return Json(new { success, message, data });
            #endregion
        }
    }
}