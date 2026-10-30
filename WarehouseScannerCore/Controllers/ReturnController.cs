using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    // 页面权限用特性声明
    [RequirePermission(MenuCodes.ReturnMaterial)]
    public class ReturnController : BaseController
    {
        #region 依赖注入 / 常量配置
        private readonly IConfiguration _configuration;
        private readonly FactoryConfigService _factoryService;
        private readonly IEpicorBusinessServiceFactory _businessServiceFactory;
        private readonly IBusinessLockProvider _lockProvider;
        private readonly ReturnBusinessService _returnBusinessService;
        private readonly IAntiforgery _antiforgery;

        // 锁键前缀（与 Transfer/Outbound/Issue/Receipt 在共享锁字典中相互隔离）
        private const string LockPrefix = "Return";
        private const string CacheKeyPrefix = "ReturnRequest:";

        public ReturnController(
            FactoryConfigService factoryService,
            ILanguageService languageService,
            ILogger<ReturnController> logger,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            UserMenuPermissionService permissionService,
            IMemoryCache memoryCache,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IEpicorBusinessServiceFactory businessServiceFactory,
            ILoggerFactory loggerFactory,
            IBusinessLockProvider lockProvider,
            ReturnBusinessService returnBusinessService,
            IAntiforgery antiforgery)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _factoryService = factoryService ?? throw new ArgumentNullException(nameof(factoryService));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _businessServiceFactory = businessServiceFactory ?? throw new ArgumentNullException(nameof(businessServiceFactory));
            _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
            _returnBusinessService = returnBusinessService ?? throw new ArgumentNullException(nameof(returnBusinessService));
            _antiforgery = antiforgery ?? throw new ArgumentNullException(nameof(antiforgery));
        }
        #endregion

        [HttpGet]
        public IActionResult Index()
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问退回页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                // 页面权限改由类级 [RequirePermission(MenuCodes.ReturnMaterial)] 特性统一声明（见类声明处）

                var language = Request.Cookies["Language"];
                var culture = !string.IsNullOrEmpty(language) ?
                    new CultureInfo(language) :
                    CultureInfo.CurrentUICulture;

                var userId = HttpContext.Session.GetString(SessionKeys.UserId);
                var userRole = HttpContext.Session.GetString(SessionKeys.UserRole);
                var siteName = EpicorSessionHelper.GetSiteCode(HttpContext.Session);
                var loginTime = HttpContext.Session.GetString(SessionKeys.LoginTime);

                ViewBag.GetText = CreateGetTextFunc();
                ViewBag.CurrentCulture = culture.Name;
                ViewBag.CurrentUICulture = culture.Name;
                ViewBag.UserId = userId;
                ViewBag.UserRole = userRole;
                ViewBag.SiteName = siteName;
                ViewBag.LoginTime = loginTime;

                return View();
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("MaterialReturn", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问退回页面时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }

        #region 验证接口

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidateJob([FromBody] ValidateReturnJobRequest request)
        {
            try
            {
                var authService = CreateEpicorAuthServiceFromSession();

                var jobList = await authService.QueryJobMtlIssued(
                    request.WorkOrderNo,
                    request.AssemblySeq,
                    ""
                );

                if (authService.HasApiError) // 优先检查 Epicor API 是否返回错误
                    return Json(new { success = false, message = string.Format(GetText("MaterialReturn", "EpicorApiFailed"), authService.LastApiError) });

                var job = jobList?.FirstOrDefault();
                if (job == null)
                {
                    var message = string.Format(GetText("MaterialReturn", "JobNotFound"),
                        request.WorkOrderNo, request.AssemblySeq);
                    return Json(new { success = false, message = message });
                }

                var successMessage = GetText("MaterialReturn", "JobValidationPassed");
                return Json(new { success = true, message = successMessage });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("MaterialReturn", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证工单出错");
                return Json(new { success = false, message = GetText("MaterialReturn", "ValidateJobError") });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidateInventory([FromBody] ValidateReturnInventoryRequest request)
        {
            try
            {
                var authService = CreateEpicorAuthServiceFromSession();

                var jobList = await authService.QueryJobMtlIssued(
                   request.WorkOrderNo,
                   request.AssemblySeq,
                    request.MaterialCode
               );

                if (authService.HasApiError) // 优先检查 Epicor API 是否返回错误
                    return Json(new { success = false, message = string.Format(GetText("MaterialReturn", "EpicorApiFailed"), authService.LastApiError) });

                var job = jobList?.FirstOrDefault();
                if (job == null)
                {
                    var message = string.Format(GetText("MaterialReturn", "MaterialNotFound"),
                        request.WorkOrderNo, request.MaterialCode);
                    return Json(new { success = false, message = message });
                }

                // 别用 ToUpper：土耳其语环境下 i/I 会转错，Unit 为空还会崩
                if (!string.Equals(job.DspIUM, request.Unit, StringComparison.OrdinalIgnoreCase))
                {
                    var message = string.Format(GetText("MaterialReturn", "UnitMismatch"),
                        request.Unit, job.DspIUM);
                    return Json(new { success = false, message = message });
                }

                decimal issuedQty = job.QtyPreviouslyIssued;
                if (issuedQty <= 0)
                {
                    var message = string.Format(GetText("MaterialReturn", "NoMaterialIssued"),
                        request.MaterialCode);
                    return Json(new { success = false, message = message });
                }

                if (string.IsNullOrWhiteSpace(request.FromWarehouse))
                {
                    return Json(new { success = false, message = GetText("MaterialReturn", "WarehouseRequired"), availableQty = 0 });
                }

                var binInfo = await authService.GetBinInfo(
                    warehouseCode: request.FromWarehouse,
                    binNum: string.IsNullOrWhiteSpace(request.Location) ? "" : request.Location
                );

                // 优先检查 Epicor API 是否失败（区别于"库位确实不存在"），避免误报
                if (authService.HasApiError)
                    return Json(new { success = false, message = string.Format(GetText("MaterialReturn", "WarehouseOrBinNotFound"), authService.LastApiError), availableQty = 0 });

                if (binInfo == null || string.IsNullOrEmpty(binInfo.BinNum))
                {
                    return Json(new { success = false, message = string.Format(GetText("MaterialReturn", "BinNotFound"), request.FromWarehouse, request.Location), availableQty = 0 });
                }

                // 查询该物料在指定仓库/库位/批次的实际库存数量（自动翻页取全部匹配行后汇总，避免单页截断）
                decimal stockQty = 0;
                var stockList = await authService.GetPartNumInventoryAll(
                    request.MaterialCode, request.FromWarehouse, request.Location, request.BatchNo);
                if (!authService.HasApiError && stockList != null && stockList.Any())
                {
                    stockQty = stockList.Sum(s => s.QtyOnHand);
                }

                // 扫码数量超过已发数量时，弹出提示（不清空表单），建议填入已发数量
                if (request.Quantity > 0 && request.Quantity > issuedQty)
                {
                    var message = string.Format(GetText("MaterialReturn", "QuantityExceedsIssued"),
                        request.Quantity, issuedQty);
                    return Json(new { success = true, message = message, suggestedQty = issuedQty, availableQty = stockQty });
                }

                var successMessage = GetText("MaterialReturn", "InventoryValidationPassed");
                return Json(new { success = true, message = successMessage, availableQty = stockQty });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("MaterialReturn", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证库存出错");
                return Json(new { success = false, message = GetText("MaterialReturn", "OperationFailedRetry") });
            }
        }

        #endregion

        [HttpPost]
        public async Task<IActionResult> ReturnMaterial([FromForm] MaterialReturnModel model, [FromHeader(Name = "X-Request-ID")] string? requestId = null)
        {
            #region AntiForgery Token 手动验证
            // 手动验证 AntiForgery Token（替代 [ValidateAntiForgeryToken] Filter）
            // 原因：Filter 在校验失败时设置 StatusCode=400 且不抛异常，
            // 对于 AJAX 请求，中间件无法正确处理并返回 JSON 错误消息。
            // 手动验证可以在 Token 失效时返回合适的 JSON 错误。
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (AntiforgeryValidationException ex)
            {
                _logger.LogWarning(ex, "物料退回 AntiForgery Token 校验失败");
                return Json(new { success = false, message = GetText("MaterialReturn", "AntiforgeryFailed") });
            }
            #endregion

            #region 参数校验
            if (!ModelState.IsValid)
            {
                // 收集具体的验证错误信息，返回给前端显示
                var errors = ModelState
                    .Where(x => x.Value!.Errors.Count > 0)
                    .SelectMany(x => x.Value!.Errors.Select(e => e.ErrorMessage))
                    .ToList();

                string errorMessage = errors.Count > 0 ? string.Join("；", errors) : GetText("MaterialReturn", "InvalidParameters");
                _logger.LogWarning("物料退回参数验证失败: {Errors}", errorMessage);
                return Json(new { success = false, message = errorMessage });
            }

            if (string.IsNullOrWhiteSpace(model.WorkOrderNo))
                return Json(new { success = false, message = GetText("MaterialReturn", "WorkOrderRequired") });
            if (model.Quantity <= 0)
                return Json(new { success = false, message = GetText("MaterialReturn", "QuantityRequired") });
            #endregion

            #region 调用业务服务
            string operatorId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";

            var (success, message, data) = await _returnBusinessService.ReturnMaterialAsync(
                model, operatorId, requestId);

            return Json(new { success, message, data });
            #endregion
        }

        #region 请求模型

        public class ValidateReturnJobRequest
        {
            [Required(ErrorMessage = "工单号不能为空")]
            public string WorkOrderNo { get; set; } = null!;

            [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
            public int AssemblySeq { get; set; }
        }

        public class ValidateReturnInventoryRequest
        {
            [Required(ErrorMessage = "工单号不能为空")]
            public string WorkOrderNo { get; set; } = null!;

            [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
            public int AssemblySeq { get; set; }

            [Required(ErrorMessage = "物料代码不能为空")]
            public string MaterialCode { get; set; } = null!;

            [Required(ErrorMessage = "单位不能为空")]
            public string Unit { get; set; } = null!;

            public string FromWarehouse { get; set; } = null!;
            public string Location { get; set; } = null!;

            [Range(0.0001, double.MaxValue, ErrorMessage = "数量必须 > 0")]
            public decimal Quantity { get; set; }

            public string BatchNo { get; set; } = null!;
        }

        #endregion
    }
}