using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    // 页面权限用特性声明；库存调整归属"盘点扫码"菜单
    [RequirePermission(MenuCodes.Inventory)]
    public class AdjustmentController : BaseController
    {
        private readonly IConfiguration _configuration;
        private readonly FactoryConfigService _factoryService;
        private readonly IBusinessLockProvider _lockProvider;

        // 锁键前缀（与 Return/Transfer/Outbound/Issue/Receipt/CountTag 在共享锁字典中相互隔离）
        private const string LockPrefix = "Adjustment";
        private const string CacheKeyPrefix = "AdjustmentRequest:";

        public AdjustmentController(
            FactoryConfigService factoryService,
            ILanguageService languageService,
            ILogger<AdjustmentController> logger,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IMemoryCache memoryCache,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IBusinessLockProvider lockProvider,
            DocumentOperationLogService documentOperationLogger,
            UserMenuPermissionService permissionService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _factoryService = factoryService ?? throw new ArgumentNullException(nameof(factoryService));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
            _documentOperationLogger = documentOperationLogger ?? throw new ArgumentNullException(nameof(documentOperationLogger));
        }

        /// <summary>每日单据操作明细日志服务</summary>
        private readonly DocumentOperationLogService _documentOperationLogger;

        [HttpGet]
        public IActionResult Index()
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问库存调整页面，重定向到登录页");
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
                _logger.LogError(ex, "访问库存调整页面时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }

        #region 验证接口
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> ValidateSourceInventory([FromBody] ValidateSourceRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.materialCode))
                {
                    return Json(new { success = false, message = GetLocalText("Adjustment", "MaterialCodeRequired") });
                }

                if (string.IsNullOrWhiteSpace(request.whseCode))
                {
                    return Json(new { success = false, message = GetLocalText("Adjustment", "FromWarehouseRequired") });
                }

                EpicorAuthService authService;
                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    return Json(new { success = false, message = GetLocalText("Adjustment", "SessionExpired") });
                }

                // 入参校验：数量必须大于 0（放在库存查询之前，避免无效查询）
                if (request.quantity <= 0)
                {
                    return Json(new { success = false, message = GetLocalText("Adjustment", "QuantityPositiveRequired") });
                }

                // 自动翻页取全部匹配行，并按 物料+仓库+库位+批次 汇总——
                // 旧逻辑单页只返回 pageSize 行且用 First() 取首行，会取错行/少算库存。
                var inventoryList = await authService.GetPartNumInventoryAll(
                    request.materialCode,
                    whseCode: request.whseCode,
                    binNum: request.binNum,
                    lotNumber: request.lotNumber
                );

                if (inventoryList == null || inventoryList.Count == 0)
                {
                    return Json(new { success = false, message = GetLocalText("Adjustment", "NoAvailableInventory") });
                }

                // 库存调整保留「已分配」语义：可用量 = Σ 在手数量 - Σ 已分配数量
                decimal qtyOnHand = inventoryList.Sum(x => x.QtyOnHand);
                decimal qtyAllocated = inventoryList.Sum(x => x.QtyAllocated);
                string systemUnit = inventoryList[0].IUM ?? string.Empty;
                decimal availableQty = qtyOnHand - qtyAllocated;

                // 先判"无可用库存"，再判"数量超过库存"（旧顺序后者恒不可达）
                if (availableQty <= 0)
                {
                    return Json(new { success = false, message = GetLocalText("Adjustment", "InsufficientInventory") });
                }

                if (availableQty < request.quantity)
                {
                    return Json(new { success = false, message = GetLocalText("Adjustment", "QuantityExceedsInventory") });
                }

                if (!string.Equals(systemUnit, request.unit, StringComparison.OrdinalIgnoreCase))
                {
                    var message = string.Format(GetLocalText("Adjustment", "UnitMismatch"), request.unit, systemUnit);
                    return Json(new { success = false, message = message });
                }

                return Json(new
                {
                    success = true,
                    message = GetLocalText("Adjustment", "SourceInventoryValid"),
                    availableQuantity = availableQty,
                    qtyOnHand = qtyOnHand,
                    qtyAllocated = qtyAllocated
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证源库存时发生异常");
                return Json(new { success = false, message = GetLocalText("Adjustment", "ValidateSourceError") });
            }
        }
        #endregion

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AdjustmentInventory([FromBody] AdjustmentModel model, [FromHeader(Name = "X-Request-ID")] string? requestId = null)
        {
            string traceId = _lockProvider.GetTraceId(HttpContext);

            // [FromBody] 绑定失败（请求体为空/非法 JSON/Content-Type 不匹配）时 model 为 null。
            // 本控制器未标注 [ApiController]，框架不会自动返回 400，必须在任何解引用前显式拒绝。
            if (model == null)
                return Json(new { success = false, message = GetLocalText("Common", "InvalidRequest") });

            // 调整原因属审计关键信息：ERP 侧只有固定 ReasonCode（RC42），不含操作员填写的原因，
            // 因此写入单据操作明细与服务端日志。换行会破坏"按行"日志格式，先清洗成单行。
            string remarkText = string.IsNullOrWhiteSpace(model.Remark)
                ? string.Empty
                : model.Remark.Replace('\r', ' ').Replace('\n', ' ').Trim();
            string remarkSuffix = string.IsNullOrEmpty(remarkText) ? string.Empty : $"/原因{remarkText}";

            // 幂等缓存键加租户前缀：跨站点同业务键不得误判"已处理过"
            string idempotentCacheKey = $"{CurrentTenantKey}|{CacheKeyPrefix}";

            // 幂等命中：必须按上次的结果码区分应答，否则会把"上次失败/结果未知"误报成"成功"
            var previousResult = _lockProvider.TryGetRequestResult(idempotentCacheKey, requestId);
            if (previousResult != null)
            {
                bool previousSucceeded = previousResult == IBusinessLockProvider.RequestResultSuccess;
                return Json(new
                {
                    success = previousSucceeded,
                    message = previousSucceeded
                        ? GetLocalText("Adjustment", "AdjustmentSuccess")
                        : GetLocalText("Common", "IdempotentResultUnknown"),
                    isIdempotent = true
                });
            }

            if (string.IsNullOrWhiteSpace(model.MaterialCode))
                return Json(new { success = false, message = GetLocalText("Adjustment", "MaterialCodeRequired") });
            if (string.IsNullOrWhiteSpace(model.FromWarehouse))
                return Json(new { success = false, message = GetLocalText("Adjustment", "FromWarehouseRequired") });
            if (string.IsNullOrWhiteSpace(model.FromLocation))
                return Json(new { success = false, message = GetLocalText("Adjustment", "FromLocationRequired") });
            // 批次为业务必填字段（库存按 仓库/库位/批次 唯一定位），缺失时不得以 ! 强行传入服务
            if (string.IsNullOrWhiteSpace(model.FromBatchNo))
                return Json(new { success = false, message = GetLocalText("Adjustment", "FromBatchNoRequired") });
            string fromBatchNo = model.FromBatchNo ?? string.Empty;
            if (model.Quantity <= 0)
                return Json(new { success = false, message = GetLocalText("Adjustment", "QuantityRequired") });
            if (string.IsNullOrWhiteSpace(model.UOM))
                return Json(new { success = false, message = GetLocalText("Adjustment", "UOMRequired") });

            // 锁键带租户（公司|工厂）：业务锁为应用级单例，缺租户会跨站点误阻塞
            string lockKey = $"{CurrentTenantKey}|{model.MaterialCode}_{model.FromWarehouse}_{model.FromLocation}_{model.FromBatchNo ?? string.Empty}";
            string operatorIdForLock = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";
            SemaphoreSlim? adjustLock = await _lockProvider.TryAcquireLockAsync(LockPrefix, lockKey, traceId, operatorIdForLock);
            if (adjustLock == null)
                return Json(new { success = false, message = GetLocalText("Adjustment", "SystemBusy") });
            try
            {
                // 锁内二次幂等检查（D2）：并发同 RequestId 的重复请求在此被拦截，
                // 避免"加锁前检查通过 → 等锁期间前一个请求已过账 → 锁内重复过账"
                var lockedResult = _lockProvider.TryGetRequestResult(idempotentCacheKey, requestId);
                if (lockedResult != null)
                {
                    bool lockedSucceeded = lockedResult == IBusinessLockProvider.RequestResultSuccess;
                    _logger.LogInformation("[{TraceId}] 库存调整幂等命中（锁内二次检查）：RequestId={RequestId}，结果={Result}",
                        traceId, requestId, lockedResult);
                    return Json(new
                    {
                        success = lockedSucceeded,
                        message = lockedSucceeded
                            ? GetLocalText("Adjustment", "AdjustmentSuccess")
                            : GetLocalText("Common", "IdempotentResultUnknown"),
                        isIdempotent = true
                    });
                }

                EpicorAuthService authService;
                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogWarning("[{TraceId}] 库存调整失败：会话已过期，物料 {PartNum}", traceId, model.MaterialCode);
                    return Json(new { success = false, message = GetLocalText("Adjustment", "SessionExpired") });
                }

                string operatorId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";

                var adjustmentService = new EpicorAdjustmentService(authService, authService.Company);
                var result = await adjustmentService.AdjustmentInventoryAsync(
                    partNum: model.MaterialCode,
                    quantity: model.Quantity,
                    fromWarehouse: model.FromWarehouse,
                    fromBinNum: model.FromLocation,
                    fromLotNum: fromBatchNo,
                    remark: model.Remark
                );

                // API 错误检查（与 TransferController 一致）：避免静默失败导致库存调整丢失
                if (result == null || authService.HasApiError)
                {
                    string errMsg = authService.LastApiError ?? "库存调整接口调用失败";
                    _logger.LogError("[{TraceId}] 库存调整失败：物料 {PartNum}，数量 {Quantity}，原因 {Remark}，错误: {Error}",
                        traceId, model.MaterialCode, model.Quantity, remarkText, errMsg);
                    _documentOperationLogger.Log("库存调整",
                        $"物料{model.MaterialCode}/数量{model.Quantity}/{model.FromWarehouse}-{model.FromLocation}{remarkSuffix}",
                        operatorId, success: false);
                    // 幂等区分两类失败：
                    // - 确定性失败：响应体中能提取到 BO 错误信息（顶层 ErrorMessage，或嵌套 returnObj.ErrorMessage，
                    //   与 CheckEpicorApiError 判定口径一致），说明 Epicor 明确拒绝、事务未提交，
                    //   不落"未知"标记，操作员修正数据后用同一 X-Request-ID 重试应允许重执行；
                    // - 真未知失败：空响应或仅有 HasApiError 但取不到 BO 错误体（传输/超时类），
                    //   有可能 ERP 已经调完只是回包没读出来，仍落"未知"标记拦截重放。
                    string? boError = result?["ErrorMessage"]?.ToString();
                    if (string.IsNullOrWhiteSpace(boError) && result?["returnObj"] is Newtonsoft.Json.Linq.JObject retObj)
                        boError = retObj["ErrorMessage"]?.ToString();
                    bool deterministicFailure = !string.IsNullOrWhiteSpace(boError);
                    if (!deterministicFailure && !string.IsNullOrEmpty(requestId))
                    {
                        _lockProvider.MarkRequestProcessed(idempotentCacheKey, requestId, IBusinessLockProvider.RequestResultUnknown);
                    }
                    return Json(new { success = false, message = string.Format(GetLocalText("Adjustment", "EpicorApiCallFailed"), authService.LastApiError) });
                }

                if (!string.IsNullOrEmpty(requestId))
                {
                    // Epicor 调整已成功，幂等标记失败不应改变业务结果（否则用户重试会重复调整）
                    try
                    {
                        _lockProvider.MarkRequestProcessed(idempotentCacheKey, requestId);
                    }
                    catch (Exception markEx)
                    {
                        _logger.LogWarning(markEx, "[{TraceId}] MarkRequestProcessed 失败（Epicor 调整已成功，仅幂等标记失败）：RequestId {RequestId}",
                            traceId, requestId);
                    }
                }

                // 单据操作明细：调整提交成功
                _documentOperationLogger.Log("库存调整",
                    $"物料{model.MaterialCode}/数量{model.Quantity}/{model.FromWarehouse}-{model.FromLocation}{remarkSuffix}",
                    operatorId, success: true);

                _logger.LogInformation("库存调整成功：物料 {PartNum}，数量 {Quantity}，单位 {UOM}，位置 {Location}，原因 {Remark}，操作员 {Operator}",
                    model.MaterialCode, model.Quantity, model.UOM, $"{model.FromWarehouse}/{model.FromLocation}", remarkText, operatorId);

                return Json(new
                {
                    success = true,
                    message = GetLocalText("Adjustment", "AdjustmentSuccess"),
                    data = new
                    {
                        PartNum = model.MaterialCode,
                        Quantity = model.Quantity,
                        UOM = model.UOM,
                        FromWarehouse = model.FromWarehouse,
                        FromBatch = model.FromBatchNo,
                        FromLocation = model.FromLocation,
                        Operator = operatorId,
                        AdjustmentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "库存调整过程中发生异常：物料 {PartNum}", model.MaterialCode);

                // 单据操作明细：调整提交异常按失败记录
                _documentOperationLogger.Log("库存调整",
                    $"物料{model?.MaterialCode}/数量{model?.Quantity}/{model?.FromWarehouse}-{model?.FromLocation}{remarkSuffix}",
                    HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown", success: false);

                // 同上：异常也可能发生在 ERP 已经调完之后；按"未知"落库
                if (!string.IsNullOrEmpty(requestId))
                {
                    _lockProvider.MarkRequestProcessed(idempotentCacheKey, requestId, IBusinessLockProvider.RequestResultUnknown);
                }

                // 仅放行具备 Epicor 业务错误特征的异常文本（"[数字] ..." 或含 CorrelationId），
                // 其余异常（网络/参数/空引用等）返回通用提示，避免泄露内部信息。
                string? epicorMessage = BaseBusinessService.ExtractEpicorBusinessError(ex);
                string message = !string.IsNullOrWhiteSpace(epicorMessage)
                    ? epicorMessage
                    : GetLocalText("Adjustment", "AdjustmentFailed");
                return Json(new { success = false, message });
            }
            finally
            {
                _lockProvider.ReleaseLock(adjustLock, LockPrefix, lockKey, traceId);
            }
        }
    }

    public class AdjustmentModel
    {
        [Required]
        public string MaterialCode { get; set; } = null!;
        public decimal Quantity { get; set; }
        [Required]
        public string UOM { get; set; } = null!;
        [Required]
        public string FromWarehouse { get; set; } = null!;
        [Required]
        public string FromBatchNo { get; set; } = null!;
        [Required]
        public string FromLocation { get; set; } = null!;
        public string Remark { get; set; } = null!;
        public string Operator { get; set; } = null!;
        public string OperatorId { get; set; } = null!;
    }
}