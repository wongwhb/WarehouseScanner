using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    // 页面权限用特性声明
    [RequirePermission(MenuCodes.Inventory)]
    public class CountTagController : BaseController
    {
        private readonly IConfiguration _configuration;
        private readonly FactoryConfigService _factoryService;
        private readonly IBusinessLockProvider _lockProvider;
        private readonly IEpicorQueryGateway _queryGateway;
        /// <summary>每日单据操作明细日志服务</summary>
        private readonly DocumentOperationLogService _documentOperationLogger;

        // 锁键前缀（与 Return/Transfer/Outbound/Issue/Receipt 在共享锁字典中相互隔离）
        private const string LockPrefix = "CountTag";
        private const string CacheKeyPrefix = "CountTagRequest:";

        public CountTagController(
            FactoryConfigService factoryService,
            ILanguageService languageService,
            ILogger<CountTagController> logger,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IMemoryCache memoryCache,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IBusinessLockProvider lockProvider,
            IEpicorQueryGateway queryGateway,
            DocumentOperationLogService documentOperationLogger,
            UserMenuPermissionService permissionService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _factoryService = factoryService ?? throw new ArgumentNullException(nameof(factoryService));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
            _queryGateway = queryGateway ?? throw new ArgumentNullException(nameof(queryGateway));
            _documentOperationLogger = documentOperationLogger ?? throw new ArgumentNullException(nameof(documentOperationLogger));
        }

        [HttpGet]
        public IActionResult Index()
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问标签计数页面，重定向到登录页");
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
                _logger.LogError(ex, "访问标签计数页面时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }

        #region 验证接口
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> ValidateCountTag([FromBody] ValidateSourceRequest request)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";

            try
            {
                if (string.IsNullOrWhiteSpace(request.materialCode))
                {
                    return Json(new { success = false, message = GetLocalText("CountTag", "MaterialCodeRequired") });
                }

                if (string.IsNullOrWhiteSpace(request.whseCode))
                {
                    return Json(new { success = false, message = GetLocalText("CountTag", "FromWarehouseRequired") });
                }

                EpicorAuthService authService;
                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    return Json(new { success = false, message = GetLocalText("CountTag", "SessionExpired") });
                }

                // 调用共享验证方法：按物料代码查询盘点标签，比较仓库/库位/批次
                var (ccTagArray, _, errorMessage) = await ValidateCountTagDataAsync(
                    authService, request.materialCode,
                    request.whseCode, request.binNum, request.lotNumber,
                    language);

                if (ccTagArray == null)
                {
                    return Json(new { success = false, message = errorMessage });
                }

                return Json(new
                {
                    success = true,
                    message = GetLocalText("CountTag", "CountTagValid")
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证盘点标签时发生异常");
                return Json(new { success = false, message = GetLocalText("CountTag", "CountTagValidationError") });
            }
        }
        #endregion

        #region 库存查询接口
        /// <summary>
        /// 根据物料+仓库+库位+批次查询库存数量（扫码解析后前端用于显示库存）
        /// 调用 Epicor BAQ PartBin 查询：现存量 QtyOnHand、已分配量 QtyAllocated、可用量 AvailableQty
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> QueryInventoryOnHand([FromBody] ValidateSourceRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.materialCode))
                    return Json(new { success = false, message = GetLocalText("CountTag", "MaterialCodeRequired") });

                var materialCode = request.materialCode.Trim();
                var whseCode = request.whseCode?.Trim() ?? string.Empty;
                var binNum = request.binNum?.Trim() ?? string.Empty;
                var lotNumber = request.lotNumber?.Trim() ?? string.Empty;

                var inventories = await _queryGateway.GetPartNumInventory(materialCode, whseCode, binNum, lotNumber);
                if (inventories == null || inventories.Count == 0)
                {
                    return Json(new
                    {
                        success = true,
                        found = false,
                        qtyOnHand = 0m,
                        qtyAllocated = 0m,
                        availableQty = 0m,
                        partDesc = string.Empty,
                        ium = string.Empty,
                        message = GetLocalText("CountTag", "NoInventoryRecord")
                    });
                }

                decimal totalOnHand = 0m, totalAllocated = 0m;
                string partDesc = inventories[0].PartDesc ?? string.Empty;
                string ium = inventories[0].IUM ?? string.Empty;
                foreach (var inv in inventories)
                {
                    totalOnHand += inv.QtyOnHand;
                    totalAllocated += inv.QtyAllocated;
                    if (string.IsNullOrEmpty(ium) && !string.IsNullOrEmpty(inv.IUM)) ium = inv.IUM;
                    if (string.IsNullOrEmpty(partDesc) && !string.IsNullOrEmpty(inv.PartDesc)) partDesc = inv.PartDesc;
                }

                return Json(new
                {
                    success = true,
                    found = true,
                    qtyOnHand = totalOnHand,
                    qtyAllocated = totalAllocated,
                    availableQty = totalOnHand - totalAllocated,
                    partDesc = partDesc,
                    ium = ium,
                    hitCount = inventories.Count,
                    message = string.Empty
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetLocalText("CountTag", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询盘点扫码库存异常: Material={PartNum}, Whse={Whse}, Bin={Bin}, Lot={Lot}",
                    request?.materialCode, request?.whseCode, request?.binNum, request?.lotNumber);
                return Json(new { success = false, message = GetLocalText("CountTag", "CountTagValidationError") });
            }
        }
        #endregion

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CountTagInventory([FromBody] CountTagModel model, [FromHeader(Name = "X-Request-ID")] string? requestId = null)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";

            string traceId = _lockProvider.GetTraceId(HttpContext);

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
                        ? GetLocalText("CountTag", "CountTagSuccess")
                        : GetLocalText("Common", "IdempotentResultUnknown"),
                    isIdempotent = true
                });
            }

            if (string.IsNullOrWhiteSpace(model.MaterialCode))
                return Json(new { success = false, message = GetLocalText("CountTag", "MaterialCodeRequired") });
            if (string.IsNullOrWhiteSpace(model.FromWarehouse))
                return Json(new { success = false, message = GetLocalText("CountTag", "FromWarehouseRequired") });
            if (string.IsNullOrWhiteSpace(model.FromLocation))
                return Json(new { success = false, message = GetLocalText("CountTag", "FromLocationRequired") });
            if (model.Quantity <= 0)
                return Json(new { success = false, message = GetLocalText("CountTag", "QuantityRequired") });

            // 锁键带租户（公司|工厂）：业务锁为应用级单例，缺租户会跨站点误阻塞
            string lockKey = $"{CurrentTenantKey}|{model.MaterialCode}_{model.FromWarehouse}_{model.FromLocation}_{model.FromBatchNo ?? string.Empty}";
            string operatorIdForLock = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";
            SemaphoreSlim? countTagLock = await _lockProvider.TryAcquireLockAsync(LockPrefix, lockKey, traceId, operatorIdForLock);
            if (countTagLock == null)
            {
                return Json(new { success = false, message = GetLocalText("CountTag", "SystemBusy") });
            }
            EpicorAuthService authService = null!;
            try
            {
                // 锁内二次幂等检查（D2）：并发同 RequestId 的重复请求在此被拦截，
                // 避免"加锁前检查通过 → 等锁期间前一个请求已过账 → 锁内重复过账"
                var lockedResult = _lockProvider.TryGetRequestResult(idempotentCacheKey, requestId);
                if (lockedResult != null)
                {
                    bool lockedSucceeded = lockedResult == IBusinessLockProvider.RequestResultSuccess;
                    _logger.LogInformation("[{TraceId}] 盘点标签幂等命中（锁内二次检查）：RequestId={RequestId}，结果={Result}",
                        traceId, requestId, lockedResult);
                    return Json(new
                    {
                        success = lockedSucceeded,
                        message = lockedSucceeded
                            ? GetLocalText("CountTag", "CountTagSuccess")
                            : GetLocalText("Common", "IdempotentResultUnknown"),
                        isIdempotent = true
                    });
                }

                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogWarning("[{TraceId}] 盘点标签失败：会话已过期，物料 {PartNum}", traceId, model.MaterialCode);
                    return Json(new { success = false, message = GetLocalText("CountTag", "SessionExpired") });
                }

                string operatorId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";

                #region 步骤1：验证盘点标签（按物料代码查询，比较仓库/库位/批次）
                var (ccTagArray, matchedIndex, errorMsg) = await ValidateCountTagDataAsync(
                    authService, model.MaterialCode,
                    model.FromWarehouse, model.FromLocation, model.FromBatchNo!,
                    language);

                if (ccTagArray == null)
                    return Json(new { success = false, message = errorMsg });
                #endregion

                #region 步骤2：构建 Update 请求数据
                var dsObj = new Newtonsoft.Json.Linq.JObject();
                dsObj["CCTag"] = ccTagArray.DeepClone() as Newtonsoft.Json.Linq.JArray;
                dsObj["CCTagWrkSht"] = new Newtonsoft.Json.Linq.JArray();
                dsObj["ExtensionTables"] = new Newtonsoft.Json.Linq.JArray();

                // 写回匹配到的那一条标签（多标签时不能固定写第 0 条）
                var ccTag = dsObj["CCTag"]![matchedIndex] as Newtonsoft.Json.Linq.JObject;
                if (ccTag != null)
                {
                    ccTag["TagReturned"] = true;
                    ccTag["TagPrinted"] = true;
                    ccTag["CountedDate"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    ccTag["CountedBy"] = operatorId;
                    ccTag["CountedQty"] = model.Quantity;
                    ccTag["RowMod"] = "U";
                }

                var updateRequest = new Newtonsoft.Json.Linq.JObject();
                updateRequest["ds"] = dsObj;
                updateRequest["parameters"] = new Newtonsoft.Json.Linq.JObject();
                updateRequest["parameters"]!["morePages"] = false;
                #endregion

                #region 步骤3：调用 Update API
                var updateUrl = "Erp.BO.CountTagSvc/Update";
                var updateResponse = await authService.PostBoJsonAsync(updateUrl, updateRequest.ToString());

                if (string.IsNullOrWhiteSpace(updateResponse))
                {
                    var msg = authService.HasApiError
                        ? $"{GetLocalText("CountTag", "CountTagFailed")}：{authService.LastApiError}"
                        : GetLocalText("CountTag", "CountTagFailed");
                    _logger.LogError("[{TraceId}] 盘点标签失败（Epicor Update 返回空）：物料 {PartNum}，错误 {Error}",
                        traceId, model.MaterialCode, authService.LastApiError ?? msg);
                    _documentOperationLogger.Log("盘点标签",
                        $"物料{model.MaterialCode}/数量{model.Quantity}/{model.FromWarehouse}-{model.FromLocation}",
                        operatorId, success: false);
                    // 结果未知：有可能 ERP 已经更新完，只是回包没读出来，必须拦住重放；
                    // 按"未知"落库，重放时才不会被误报成成功
                    if (!string.IsNullOrEmpty(requestId))
                    {
                        _lockProvider.MarkRequestProcessed(idempotentCacheKey, requestId, IBusinessLockProvider.RequestResultUnknown);
                    }
                    return Json(new { success = false, message = msg });
                }

                var updateObj = Newtonsoft.Json.Linq.JObject.Parse(updateResponse);
                var hasErrorToken = updateObj.SelectToken("returnObj.HasError");
                var hasError = hasErrorToken != null ? hasErrorToken.ToObject<bool>() : false;
                var errorMessage = updateObj.SelectToken("returnObj.Message")?.ToString();

                if (hasError || !string.IsNullOrEmpty(errorMessage))
                {
                    _logger.LogError("[{TraceId}] 盘点标签失败（Epicor Update HasError）：物料 {PartNum}，错误 {Error}",
                        traceId, model.MaterialCode, errorMessage ?? "HasError=true");
                    _documentOperationLogger.Log("盘点标签",
                        $"物料{model.MaterialCode}/数量{model.Quantity}/{model.FromWarehouse}-{model.FromLocation}",
                        operatorId, success: false);
                    // 确定性失败：Epicor Update 在 200 响应中明确返回失败结果（returnObj.HasError/Message），
                    // 属于 BO 业务校验拒绝、事务未提交，不落"未知"标记——
                    // 操作员修正数据后用同一 X-Request-ID 重试应允许重执行。
                    return Json(new { success = false, message = errorMessage ?? GetLocalText("CountTag", "CountTagFailed") });
                }
                #endregion

                if (!string.IsNullOrEmpty(requestId))
                {
                    // Epicor 盘点已成功，幂等标记失败不应改变业务结果（否则用户重试会重复盘点）
                    try
                    {
                        _lockProvider.MarkRequestProcessed(idempotentCacheKey, requestId);
                    }
                    catch (Exception markEx)
                    {
                        _logger.LogWarning(markEx, "[{TraceId}] MarkRequestProcessed 失败（Epicor 盘点已成功，仅幂等标记失败）：RequestId {RequestId}",
                            traceId, requestId);
                    }
                }

                // 单据操作明细：盘点标签提交成功
                _documentOperationLogger.Log("盘点标签",
                    $"物料{model.MaterialCode}/数量{model.Quantity}/{model.FromWarehouse}-{model.FromLocation}",
                    operatorId, success: true);

                _logger.LogInformation("盘点标签成功：物料 {PartNum}，数量 {Quantity}，单位 {UOM}，位置 {Location}，操作员 {Operator}",
                    model.MaterialCode, model.Quantity, model.UOM, $"{model.FromWarehouse}/{model.FromLocation}", operatorId);

                return Json(new
                {
                    success = true,
                    message = GetLocalText("CountTag", "CountTagSuccess"),
                    data = new
                    {
                        PartNum = model.MaterialCode,
                        Quantity = model.Quantity,
                        UOM = model.UOM,
                        FromWarehouse = model.FromWarehouse,
                        FromBatch = model.FromBatchNo,
                        FromLocation = model.FromLocation,
                        Operator = operatorId,
                        CountTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "盘点标签过程中发生异常：物料 {PartNum}", model.MaterialCode);

                // 单据操作明细：盘点标签提交异常按失败记录
                _documentOperationLogger.Log("盘点标签",
                    $"物料{model?.MaterialCode}/数量{model?.Quantity}/{model?.FromWarehouse}-{model?.FromLocation}",
                    HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown", success: false);

                // 同上：异常也可能发生在 ERP 已经更新完之后；按"未知"落库
                if (!string.IsNullOrEmpty(requestId))
                {
                    _lockProvider.MarkRequestProcessed(idempotentCacheKey, requestId, IBusinessLockProvider.RequestResultUnknown);
                }

                var msg = authService != null && authService.HasApiError
                    ? $"{GetLocalText("CountTag", "CountTagFailed")}：{authService.LastApiError}"
                    : GetLocalText("CountTag", "CountTagFailed");
                return Json(new { success = false, message = msg });
            }
            finally
            {
                _lockProvider.ReleaseLock(countTagLock, LockPrefix, lockKey, traceId);
            }
        }

        #region 共享验证：查询盘点标签并比较仓库/库位/批次
        /// <summary>
        /// 根据物料代码查询盘点标签，验证仓库/库位/批次是否一致。
        /// </summary>
        /// <returns>验证通过返回 ccTagArray；失败返回 null 及错误消息。</returns>
        private async Task<(Newtonsoft.Json.Linq.JArray? ccTagArray, int matchedIndex, string? errorMessage)> ValidateCountTagDataAsync(
            EpicorAuthService authService, string materialCode,
            string warehouseCode, string binNum, string lotNum,
            string language)
        {
            // 仅按物料代码过滤，查询该物料是否创建了盘点标签
            // 安全：白名单校验 + OData 单引号转义，防止扫码枪输入造成 OData 注入
            if (!ODataFilterBuilder.IsValidPartNum(materialCode))
            {
                _logger.LogWarning("盘点标签查询被拒绝：物料编码格式非法 {MaterialCode}", materialCode);
                return (null, -1, GetLocalText("CountTag", "CountTagValidationError"));
            }

            // Epicor BO URL 构造与调用统一下沉到 EpicorBoQueryHelper。
            //   仍使用本方法的 authService 实例，HasApiError / LastApiError 判错链路未变。
            var response = await EpicorBoQueryHelper.GetCountTagRowsAsync(authService, materialCode);

            if (string.IsNullOrWhiteSpace(response))
            {
                var msg = authService.HasApiError
                    ? $"{GetLocalText("CountTag", "CountTagFailed")}：{authService.LastApiError}"
                    : GetLocalText("CountTag", "CountTagNotFound");
                return (null, -1, msg);
            }

            var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(response);
            var ccTagArray = jsonObj.SelectToken("returnObj.CCTag") as Newtonsoft.Json.Linq.JArray;

            if (ccTagArray == null || ccTagArray.Count == 0)
            {
                return (null, -1, GetLocalText("CountTag", "CountTagNotFound"));
            }

            var inputWarehouse = warehouseCode?.Trim() ?? string.Empty;
            var inputBin = binNum?.Trim() ?? string.Empty;
            var inputLot = lotNum?.Trim() ?? string.Empty;

            // 在全部盘点标签中查找与扫码仓库/库位/批次匹配的行（多标签时不能只看第一条）
            int matchedIndex = -1;
            Newtonsoft.Json.Linq.JObject? firstTag = null;
            for (int i = 0; i < ccTagArray.Count; i++)
            {
                if (ccTagArray[i] is not Newtonsoft.Json.Linq.JObject tag) continue;
                firstTag ??= tag;

                var tagWarehouse = tag["WarehouseCode"]?.ToString() ?? string.Empty;
                var tagBin = tag["BinNum"]?.ToString() ?? string.Empty;
                var tagLot = tag["LotNum"]?.ToString() ?? string.Empty;

                if (string.Equals(tagWarehouse, inputWarehouse, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(tagBin, inputBin, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(tagLot, inputLot, StringComparison.OrdinalIgnoreCase))
                {
                    matchedIndex = i;
                    break;
                }
            }

            if (matchedIndex >= 0)
            {
                return (ccTagArray, matchedIndex, null);
            }

            // 单标签场景保持原有的逐项不匹配提示（行为不变）
            if (ccTagArray.Count == 1 && firstTag != null)
            {
                var tagWarehouse = firstTag["WarehouseCode"]?.ToString() ?? string.Empty;
                var tagBin = firstTag["BinNum"]?.ToString() ?? string.Empty;
                var tagLot = firstTag["LotNum"]?.ToString() ?? string.Empty;

                if (!string.Equals(tagWarehouse, inputWarehouse, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, -1, string.Format(GetLocalText("CountTag", "CountTagWarehouseMismatch"), tagWarehouse, inputWarehouse));
                }

                if (!string.Equals(tagBin, inputBin, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, -1, string.Format(GetLocalText("CountTag", "CountTagBinMismatch"), tagBin, inputBin));
                }

                if (!string.Equals(tagLot, inputLot, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, -1, string.Format(GetLocalText("CountTag", "CountTagLotMismatch"), tagLot, inputLot));
                }
            }

            if (ccTagArray.Count == 1)
            {
                return (null, -1, GetLocalText("CountTag", "CountTagValidationError"));
            }

            return (null, -1, GetLocalText("CountTag", "CountTagNoMatchingTag"));
        }
        #endregion

    }

    public class CountTagModel
    {
        public string MaterialCode { get; set; } = null!;
        public decimal Quantity { get; set; }
        public string UOM { get; set; } = null!;
        public string FromWarehouse { get; set; } = null!;
        public string FromBatchNo { get; set; } = null!;
        public string FromLocation { get; set; } = null!;
        public string Remark { get; set; } = null!;
        public string Operator { get; set; } = null!;
        public string OperatorId { get; set; } = null!;
    }
}