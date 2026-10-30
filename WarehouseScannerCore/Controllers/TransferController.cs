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
using System.Runtime;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    // 页面权限用特性声明
    [RequirePermission(MenuCodes.Transfer)]
    public class TransferController : BaseController
    {
        private readonly IConfiguration _configuration;
        private readonly FactoryConfigService _factoryService;
        private readonly IBusinessLockProvider _lockProvider;
        private readonly TransferBusinessService _transferBusinessService;

        // 锁键前缀（与 Return/Outbound/Issue/Receipt 在共享锁字典中相互隔离）
        private const string LockPrefix = "Transfer";
        private const string CacheKeyPrefix = "TransferRequest:";

        public TransferController(
            FactoryConfigService factoryService,
            ILanguageService languageService,
            ILogger<TransferController> logger,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IMemoryCache memoryCache,
            IEpicorAuthServiceFactory epicorServiceFactory,
            UserMenuPermissionService permissionService,
            IBusinessLockProvider lockProvider,
            TransferBusinessService transferBusinessService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _factoryService = factoryService ?? throw new ArgumentNullException(nameof(factoryService));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
            _transferBusinessService = transferBusinessService ?? throw new ArgumentNullException(nameof(transferBusinessService));
        }

        [HttpGet]
        public IActionResult Index()
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问库存转移页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                var language = Request.Cookies["Language"];
                var culture = !string.IsNullOrEmpty(language)
                    ? new CultureInfo(language)
                    : CultureInfo.CurrentUICulture;

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
                return RedirectToAction("Login", "Account");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问库存转移页面时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }

        #region 新增验证接口

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> ValidateSourceInventory([FromBody] ValidateSourceRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.materialCode))
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "MaterialCodeRequired") });
                }

                if (string.IsNullOrWhiteSpace(request.whseCode))
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "FromWarehouseRequired") });
                }

                EpicorAuthService authService;
                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "SessionExpired") });
                }

                // 入参校验：数量必须大于 0（放在库存查询之前，避免无效查询）
                if (request.quantity <= 0)
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "QuantityPositiveRequired") });
                }

                // 库存查询：与 TransferBusinessService.ValidateSourceInventoryAsync 同源，调 BAQ Inve_view
                // （入参 物料代码 + 仓库 + 库位 + 批次），库存数 = Σ 在手数量（Inve_view 不返回已分配数量）。
                // 不走 PartBinSearch BO——其过滤维度取值不一致时会返回 0 行，出现"有库存却提示无库存"。
                var inventoryList = await authService.ListInveViewInventoryByBAQAsync(
                    request.materialCode,
                    request.whseCode,
                    request.binNum,
                    request.lotNumber
                );

                if (authService.HasApiError) // 优先检查 Epicor API 是否返回错误
                    return Json(new { success = false, message = string.Format(GetLocalText("Transfer", "EpicorApiFailed"), authService.LastApiError) });

                if (inventoryList == null || inventoryList.Count == 0)
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "NoAvailableInventory") });
                }

                // BAQ 可能返回多行（同维度多条 PartBin），必须汇总在手数量，禁止取首行。
                decimal availableQty = inventoryList.Sum(x => x.OnhandQty);
                string systemUnit = inventoryList[0].IUM ?? string.Empty;

                // 先判"无可用库存"，再判"数量超过库存"（旧顺序后者恒不可达）
                if (availableQty <= 0)
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "InsufficientInventory") });
                }

                if (availableQty < request.quantity)
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "QuantityExceedsInventory") });
                }

                if (!string.Equals(systemUnit, request.unit, StringComparison.OrdinalIgnoreCase))
                {
                    var message = string.Format(GetLocalText("Transfer", "UnitMismatch"),
                        request.unit, systemUnit);
                    return Json(new { success = false, message = message });
                }

                return Json(new
                {
                    success = true,
                    message = GetLocalText("Transfer", "SourceInventoryValid"),
                    availableQuantity = availableQty
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证源库存时发生异常");
                return Json(new { success = false, message = GetLocalText("Transfer", "ValidateSourceError") });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> ValidateTargetBin([FromBody] ValidateBinRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.warehouseCode))
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "ToWarehouseRequired") });
                }

                if (string.IsNullOrWhiteSpace(request.binNum))
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "ToLocationRequired") });
                }

                EpicorAuthService authService;
                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "SessionExpired") });
                }

                var binInfo = await authService.GetBinInfo(
                    warehouseCode: request.warehouseCode,
                    binNum: request.binNum
                );

                if (authService.HasApiError) // 优先检查 Epicor API 是否返回错误
                    return Json(new { success = false, message = string.Format(GetLocalText("Transfer", "WarehouseOrBinNotFound"), authService.LastApiError) });

                if (binInfo == null || string.IsNullOrEmpty(binInfo.BinNum))
                    return Json(new { success = false, message = GetLocalText("Transfer", "TargetLocationNotFound") });

                return Json(new
                {
                    success = true,
                    message = GetLocalText("Transfer", "TargetLocationValid"),
                    binNum = binInfo.BinNum,
                    description = binInfo.Description
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证目标库位时发生异常");
                return Json(new { success = false, message = GetLocalText("Transfer", "OperationFailedRetry") });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> ValidateTargetLot([FromBody] ValidateLotRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.lotNumber))
                {
                    return Json(new
                    {
                        success = true,
                        message = GetLocalText("Transfer", "BatchOptional"),
                        batchExists = false,
                        isOptional = true
                    });
                }

                if (string.IsNullOrWhiteSpace(request.partNum))
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "MaterialCodeRequired") });
                }

                EpicorAuthService authService;
                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    return Json(new { success = false, message = GetText("Transfer", "SessionExpired") });
                }

                var lotInfoList = await authService.GetLotInfo(
                    company: authService.Company,
                    partNum: request.partNum,
                    lotNumber: request.lotNumber
                );

                // 查不到批次可能是接口挂了，不能让操作员以为能新建
                if (lotInfoList == null && authService.HasApiError)
                {
                    return Json(new
                    {
                        success = false,
                        message = string.Format(GetLocalText("Transfer", "EpicorApiFailed"), authService.LastApiError),
                        batchExists = false,
                        canCreateNew = false
                    });
                }

                if (lotInfoList == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = GetText("Transfer", "TargetBatchNotExists"),
                        batchExists = false,
                        canCreateNew = true
                    });
                }

                var lotInfo = lotInfoList;

                return Json(new
                {
                    success = true,
                    message = GetText("Transfer", "TargetBatchExists"),
                    batchExists = true,
                    lotNumber = lotInfo.LotNumber
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证目标批次时发生异常");
                return Json(new { success = false, message = GetLocalText("Transfer", "ValidateBatchError") });
            }
        }

        #endregion

        #region 扫码查询库存位置

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> QueryInventoryLocation([FromBody] QueryInventoryLocationRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.PartNum))
                    return Json(new { success = false, message = GetLocalText("Transfer", "MaterialCodeRequired") });

                if (string.IsNullOrWhiteSpace(request.LotNum))
                    return Json(new { success = false, message = GetLocalText("Transfer", "FromBatchRequired") });

                EpicorAuthService authService;
                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    return Json(new { success = false, message = GetLocalText("Transfer", "SessionExpired") });
                }

                // 按「物料代码 + 仓库 + 批次号」查询库存明细
                // 同一仓库同一批次可能分布在多个 BinNum，返回列表供前端手工选择源库位
                var inveList = await authService.ListInveViewInventoryByBAQAsync(
                    request.PartNum, request.WarehouseCode, null, request.LotNum);

                if (authService.HasApiError)
                    return Json(new { success = false, message = string.Format(GetLocalText("Transfer", "EpicorApiFailed"), authService.LastApiError) });

                // 按库位去重（PartBin 维度唯一，防止重复渲染候选项）
                var binList = inveList
                    .GroupBy(x => x.BinNum ?? string.Empty)
                    .Select(g => g.First())
                    .Select(x => new
                    {
                        binNum = x.BinNum,
                        warehouseCode = x.WarehouseCode,
                        lotNum = x.LotNum,
                        unit = x.IUM,
                        onhandQty = x.OnhandQty
                    })
                    .ToList();

                if (binList.Count == 0)
                    return Json(new { success = false, message = GetLocalText("Transfer", "NoAvailableInventory") });

                return Json(new
                {
                    success = true,
                    count = binList.Count,
                    unit = binList[0].unit,
                    warehouseCode = binList[0].warehouseCode,
                    binNum = binList.Count == 1 ? binList[0].binNum : string.Empty,
                    onhandQty = binList[0].onhandQty,
                    list = binList
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询库存位置时发生异常");
                return Json(new { success = false, message = GetLocalText("Transfer", "QueryLocationError") });
            }
        }

        #endregion

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TransferInventory([FromBody] TransferModel model, [FromHeader(Name = "X-Request-ID")] string? requestId = null)
        {
            #region 参数校验
            if (string.IsNullOrWhiteSpace(model.MaterialCode))
                return Json(new { success = false, message = GetLocalText("Transfer", "MaterialCodeRequired") });
            if (string.IsNullOrWhiteSpace(model.FromWarehouse))
                return Json(new { success = false, message = GetLocalText("Transfer", "FromWarehouseRequired") });
            if (string.IsNullOrWhiteSpace(model.FromLocation))
                return Json(new { success = false, message = GetLocalText("Transfer", "FromLocationRequired") });
            if (model.Quantity <= 0)
                return Json(new { success = false, message = GetLocalText("Transfer", "QuantityRequired") });
            #endregion

            #region 调用业务服务
            string operatorId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";

            var (success, message, data) = await _transferBusinessService.TransferInventoryAsync(
                model, operatorId, requestId);

            return Json(new { success, message, data });
            #endregion
        }
    }

    public class TransferModel
    {
        [Required(ErrorMessage = "物料代码不能为空")]
        public string MaterialCode { get; set; } = null!;

        [Range(0.0001, double.MaxValue, ErrorMessage = "数量必须 > 0")]
        public decimal Quantity { get; set; }

        [Required(ErrorMessage = "单位不能为空")]
        public string Unit { get; set; } = null!;

        [Required(ErrorMessage = "源仓库不能为空")]
        public string FromWarehouse { get; set; } = null!;

        public string FromBatchNo { get; set; } = null!;

        [Required(ErrorMessage = "源库位不能为空")]
        public string FromLocation { get; set; } = null!;

        [Required(ErrorMessage = "目标仓库不能为空")]
        public string ToWarehouse { get; set; } = null!;

        public string ToBatchNo { get; set; } = null!;

        [Required(ErrorMessage = "目标库位不能为空")]
        public string ToLocation { get; set; } = null!;

        public string Remark { get; set; } = null!;
        public string Operator { get; set; } = null!;
        public string OperatorId { get; set; } = null!;
    }

    public class ValidateSourceRequest
    {
        [Required(ErrorMessage = "物料代码不能为空")]
        public string materialCode { get; set; } = null!;

        [Required(ErrorMessage = "仓库代码不能为空")]
        public string whseCode { get; set; } = null!;

        public string binNum { get; set; } = null!;
        public string lotNumber { get; set; } = null!;

        [Required(ErrorMessage = "单位不能为空")]
        public string unit { get; set; } = null!;

        [Range(0.0001, double.MaxValue, ErrorMessage = "数量必须 > 0")]
        public decimal quantity { get; set; }
    }

    public class ValidateBinRequest
    {
        [Required(ErrorMessage = "仓库代码不能为空")]
        public string warehouseCode { get; set; } = null!;

        [Required(ErrorMessage = "库位不能为空")]
        public string binNum { get; set; } = null!;
    }

    public class ValidateLotRequest
    {
        public string company { get; set; } = null!;

        [Required(ErrorMessage = "物料代码不能为空")]
        public string partNum { get; set; } = null!;

        public string lotNumber { get; set; } = null!;
    }

    public class QueryInventoryLocationRequest
    {
        [Required(ErrorMessage = "物料代码不能为空")]
        public string PartNum { get; set; } = null!;

        /// <summary>仓库代码（可选；为空时按 物料+批次 查询全部仓库）</summary>
        public string WarehouseCode { get; set; } = null!;

        [Required(ErrorMessage = "批次号不能为空")]
        public string LotNum { get; set; } = null!;
    }
}