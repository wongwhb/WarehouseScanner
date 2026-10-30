using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WarehouseScanner.Services;
using WarehouseScannerCore.Controllers; // TransferModel 定义在此
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 库存转移业务服务
    /// 职责：封装库存转移流程全部核心业务逻辑（源库存验证 → 目标库位验证 → 批次验证 → 转移执行）
    /// 通用基础设施（锁 / 幂等 / TraceId / 依赖注入）由 <see cref="BaseBusinessService"/> 提供。
    /// 
    /// 架构原则：Controller 只做请求调度，业务逻辑下沉到 BusinessService
    /// </summary>
    public class TransferBusinessService : BaseBusinessService
    {
        // 锁键前缀：与 Return/Issue/Outbound/Receipt 等业务服务在共享锁字典中相互隔离
        private const string LockPrefix = "Transfer";
        private const string CacheKeyPrefix = "TransferRequest:";

        public TransferBusinessService(
            ILogger<TransferBusinessService> logger,
            IEpicorQueryGateway queryGateway,
            IEpicorBusinessServiceFactory businessServiceFactory,
            IHttpContextAccessor httpContextAccessor,
            IMemoryCache memoryCache,
            IBusinessLockProvider lockProvider,
            DocumentOperationLogService documentOperationLogger)
            : base(logger, queryGateway, businessServiceFactory, httpContextAccessor, memoryCache, lockProvider, documentOperationLogger)
        {
        }

        #region 子类标识

        protected override string LockKeyPrefix => LockPrefix;
        protected override string RequestCacheKeyPrefix => CacheKeyPrefix;

        #endregion

        #region 1. 源库存验证

        /// <summary>
        /// 验证源库存是否存在且数量充足
        /// </summary>
        /// <param name="materialCode">物料代码</param>
        /// <param name="warehouse">源仓库</param>
        /// <param name="binNum">源库位</param>
        /// <param name="lotNumber">源批次</param>
        /// <param name="quantity">转移数量</param>
        /// <param name="unit">单位</param>
        /// <returns>(success, message, availableQty)</returns>
        public async Task<(bool success, string message, decimal availableQty)> ValidateSourceInventoryAsync(
            string materialCode, string warehouse, string binNum, string? lotNumber, decimal quantity, string unit)
        {
            if (string.IsNullOrWhiteSpace(materialCode))
                return (false, GetText("Transfer", "MaterialCodeRequired"), 0);
            if (string.IsNullOrWhiteSpace(warehouse))
                return (false, GetText("Transfer", "FromWarehouseRequired"), 0);

            // 按 物料代码 + 仓库 + 库位 + 批次 四个维度查询库存 BAQ（Inve_view）；空值维度不参与过滤。
            // 与 Outbound/Issue 保持同一口径：库存数 = Σ 在手数量 QtyOnHand（Inve_view 不返回已分配数量）。
            // 不走 PartBinSearch BO——其过滤维度取值不一致时会返回 0 行，出现"有库存却提示无库存"。
            var inventoryList = await QueryGateway.GetInventoryDetailByInveViewBAQAsync(
                materialCode, warehouse, binNum, lotNumber);

            if (QueryGateway.HasApiError)
                return (false, string.Format(GetText("Transfer", "EpicorApiFailed"), QueryGateway.LastApiError), 0);

            if (inventoryList == null || inventoryList.Count == 0)
                return (false, GetText("Transfer", "NoAvailableInventory"), 0);

            // BAQ 可能返回多行（同维度多条 PartBin），必须汇总在手数量，禁止取首行。
            decimal availableQty = inventoryList.Sum(x => x.QtyOnHand);
            string systemUnit = inventoryList[0].IUM ?? string.Empty;

            if (availableQty <= 0)
                return (false, GetText("Transfer", "InsufficientInventory"), availableQty);

            if (availableQty < quantity)
                return (false, string.Format(GetText("Transfer", "QtyExceedsAvailable"), quantity, availableQty), availableQty);

            if (!string.Equals(systemUnit, unit, StringComparison.OrdinalIgnoreCase))
                return (false, string.Format(GetText("Transfer", "UnitMismatch"), unit, systemUnit), availableQty);

            return (true, GetText("Transfer", "SourceInventoryValid"), availableQty);
        }

        #endregion

        #region 2. 目标库位验证

        /// <summary>
        /// 验证目标库位是否存在
        /// </summary>
        /// <param name="warehouse">目标仓库</param>
        /// <param name="binNum">目标库位</param>
        /// <returns>(success, message, binDescription)</returns>
        public async Task<(bool success, string message, string description)> ValidateTargetBinAsync(
            string warehouse, string binNum)
        {
            if (string.IsNullOrWhiteSpace(warehouse))
                return (false, GetText("Transfer", "ToWarehouseRequired"), string.Empty);
            if (string.IsNullOrWhiteSpace(binNum))
                return (false, GetText("Transfer", "ToLocationRequired"), string.Empty);

            var authService = BusinessServiceFactory.CreateAuthService();
            var binInfo = await authService.GetBinInfo(
                warehouseCode: warehouse,
                binNum: binNum
            );

            if (authService.HasApiError)
                return (false, string.Format(GetText("Transfer", "WarehouseOrBinNotFound"), authService.LastApiError), string.Empty);

            if (binInfo == null || string.IsNullOrEmpty(binInfo.BinNum))
                return (false, GetText("Transfer", "TargetLocationNotFound"), string.Empty);

            return (true, GetText("Transfer", "TargetLocationValid"), binInfo.Description ?? string.Empty);
        }

        #endregion

        #region 3. 目标批次验证

        /// <summary>
        /// 验证目标批次是否存在（可选）
        /// </summary>
        /// <param name="partNum">物料代码</param>
        /// <param name="lotNumber">批次号</param>
        /// <returns>(success, message, batchExists, canCreateNew)</returns>
        public async Task<(bool success, string message, bool batchExists, bool canCreateNew)> ValidateTargetLotAsync(
            string partNum, string lotNumber)
        {
            // 批次号为空时，允许创建新批次
            if (string.IsNullOrWhiteSpace(lotNumber))
                return (true, GetText("Transfer", "BatchOptional"), false, true);

            if (string.IsNullOrWhiteSpace(partNum))
                return (false, GetText("Transfer", "MaterialCodeRequired"), false, false);

            var authService = BusinessServiceFactory.CreateAuthService();
            var lotInfo = await authService.GetLotInfo(
                company: authService.Company,
                partNum: partNum,
                lotNumber: lotNumber
            );

            if (authService.HasApiError)
                return (false, string.Format(GetText("Transfer", "EpicorApiFailed"), authService.LastApiError), false, false);

            if (lotInfo == null)
                return (true, GetText("Transfer", "TargetBatchNotExists"), false, true);

            return (true, GetText("Transfer", "TargetBatchExists"), true, false);
        }

        #endregion

        #region 4. 执行库存转移

        /// <summary>
        /// 执行库存转移操作
        /// 所有验证阶段只调用一次 API，结果向下传递
        /// </summary>
        /// <param name="model">转移请求模型</param>
        /// <param name="operatorId">操作员ID</param>
        /// <param name="requestId">请求ID（用于幂等性）</param>
        /// <returns>(success, message, data)</returns>
        public async Task<(bool success, string message, object? data)> TransferInventoryAsync(
            TransferModel model, string operatorId, string? requestId = null)
        {
            string traceId = GetTraceId();

            // 转移原因写入单据操作明细与服务端日志：ERP 侧无原因字段，由应用侧承担审计追溯；
            // 换行会破坏"按行"日志格式，先清洗成单行；model 判空在下方，这里用 ?. 防止空引用。
            string remarkText = string.IsNullOrWhiteSpace(model?.Remark)
                ? string.Empty
                : model.Remark.Replace('\r', ' ').Replace('\n', ' ').Trim();
            string remarkSuffix = string.IsNullOrEmpty(remarkText) ? string.Empty : $"/原因{remarkText}";

            // 参数校验
            if (model == null)
            {
                Logger.LogWarning("[{TraceId}] 转移失败：请求参数为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("Transfer", "RequestNull"), null);
            }
            if (string.IsNullOrWhiteSpace(model.MaterialCode))
            {
                Logger.LogWarning("[{TraceId}] 转移失败：物料代码为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("Transfer", "MaterialCodeRequired"), null);
            }
            if (string.IsNullOrWhiteSpace(model.FromWarehouse))
            {
                Logger.LogWarning("[{TraceId}] 转移失败：源仓库为空，物料 {MaterialCode}，操作员 {Operator}", traceId, model.MaterialCode, operatorId);
                return (false, GetText("Transfer", "FromWarehouseRequired"), null);
            }
            if (string.IsNullOrWhiteSpace(model.FromLocation))
            {
                Logger.LogWarning("[{TraceId}] 转移失败：源库位为空，物料 {MaterialCode}，操作员 {Operator}", traceId, model.MaterialCode, operatorId);
                return (false, GetText("Transfer", "FromLocationRequired"), null);
            }
            if (string.IsNullOrWhiteSpace(model.ToWarehouse))
            {
                Logger.LogWarning("[{TraceId}] 转移失败：目标仓库为空，物料 {MaterialCode}，操作员 {Operator}", traceId, model.MaterialCode, operatorId);
                return (false, GetText("Transfer", "ToWarehouseRequired"), null);
            }
            if (string.IsNullOrWhiteSpace(model.ToLocation))
            {
                Logger.LogWarning("[{TraceId}] 转移失败：目标库位为空，物料 {MaterialCode}，操作员 {Operator}", traceId, model.MaterialCode, operatorId);
                return (false, GetText("Transfer", "ToLocationRequired"), null);
            }
            if (model.Quantity <= 0)
            {
                Logger.LogWarning("[{TraceId}] 转移失败：数量无效 {Quantity}，物料 {MaterialCode}，操作员 {Operator}", traceId, model.Quantity, model.MaterialCode, operatorId);
                return (false, GetText("Transfer", "QuantityPositiveRequired"), null);
            }

            Logger.LogInformation("[{TraceId}] 转移开始：物料 {MaterialCode}，数量 {Quantity}，自 {FromWhse}/{FromLoc} 至 {ToWhse}/{ToLoc}，操作员 {Operator}",
                traceId, model.MaterialCode, model.Quantity, model.FromWarehouse, model.FromLocation, model.ToWarehouse, model.ToLocation, operatorId);

            // 幂等性前置检查：命中时必须区分"上次成功"与"结果未知"，否则会把失败误报成成功
            var previousResult = TryGetRequestResult(requestId);
            if (previousResult != null)
            {
                bool previousSucceeded = previousResult == IBusinessLockProvider.RequestResultSuccess;
                Logger.LogInformation("[{TraceId}] 幂等请求已处理: RequestId={RequestId}，结果={Result}", traceId, requestId, previousResult);
                return (previousSucceeded,
                        previousSucceeded ? GetText("Transfer", "AlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                        null);
            }

            // 工单级并发锁（基于物料+源库位+批次）
            string safeFromBatch = model.FromBatchNo ?? string.Empty;
            string lockKey = $"{model.MaterialCode}_{model.FromWarehouse}_{model.FromLocation}_{safeFromBatch}";
            var jobLock = await TryAcquireLockAsync(lockKey, traceId, operatorId);
            if (jobLock == null)
            {
                Logger.LogWarning("[{TraceId}] 转移失败：获取锁超时，物料 {MaterialCode}，操作员 {Operator}", traceId, model.MaterialCode, operatorId);
                return (false, GetText("Transfer", "SystemBusy"), null);
            }

            // 放 try 外面，catch 里才拿得到它读 ERP 报了什么错
            EpicorAuthService? authService = null;

            try
            {
                // 双重幂等性检查：命中同样必须区分成功与结果未知
                var lockedResult = TryGetRequestResult(requestId);
                if (lockedResult != null)
                {
                    bool lockedSucceeded = lockedResult == IBusinessLockProvider.RequestResultSuccess;
                    Logger.LogInformation("[{TraceId}] 幂等请求已处理（锁内二次检查）: RequestId={RequestId}，结果={Result}", traceId, requestId, lockedResult);
                    return (lockedSucceeded,
                            lockedSucceeded ? GetText("Transfer", "AlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                            null);
                }

                // ===== 验证阶段：每个 API 只调用一次 =====

                // 1. 源库存验证（1 次 API）
                var (sourceValid, sourceMessage, availableQty) = await ValidateSourceInventoryAsync(
                    model.MaterialCode,
                    model.FromWarehouse,
                    model.FromLocation,
                    model.FromBatchNo,
                    model.Quantity,
                    model.Unit
                );

                if (!sourceValid)
                {
                    Logger.LogWarning("[{TraceId}] 转移失败：源库存验证未通过，物料 {MaterialCode}，仓库 {Warehouse}，库位 {Location}，原因 {Reason}",
                        traceId, model.MaterialCode, model.FromWarehouse, model.FromLocation, sourceMessage);
                    return (false, sourceMessage, null);
                }

                // 2. 目标库位验证（1 次 API）
                var (targetBinValid, targetBinMessage, binDescription) = await ValidateTargetBinAsync(
                    model.ToWarehouse,
                    model.ToLocation
                );

                if (!targetBinValid)
                {
                    Logger.LogWarning("[{TraceId}] 转移失败：目标库位验证未通过，仓库 {Warehouse}，库位 {Location}，原因 {Reason}",
                        traceId, model.ToWarehouse, model.ToLocation, targetBinMessage);
                    return (false, targetBinMessage, null);
                }

                // 3. 目标批次验证（1 次 API）
                var (lotValid, lotMessage, batchExists, canCreateNew) = await ValidateTargetLotAsync(
                    model.MaterialCode,
                    model.ToBatchNo
                );

                if (!lotValid)
                {
                    Logger.LogWarning("[{TraceId}] 转移失败：目标批次验证未通过，物料 {MaterialCode}，批次 {Batch}，原因 {Reason}",
                        traceId, model.MaterialCode, model.ToBatchNo, lotMessage);
                    return (false, lotMessage, null);
                }

                // 4. 检查源和目标是否相同
                //    注意：不能把三个字段拼成一个字符串再比较——不同字段的拆分组合可能拼出相同结果
                //    （如 Warehouse="A" + Location="BC" 与 Warehouse="AB" + Location="C" 都得 "ABC"），
                //    因此必须逐字段按序号（Ordinal）比较。
                //    fromAddress / toAddress 仅用于日志展示，不参与相等性判定。
                string fromAddress = $"{model.FromWarehouse}{model.FromLocation}{model.FromBatchNo}";
                string toAddress = $"{model.ToWarehouse}{model.ToLocation}{model.ToBatchNo}";

                bool sameAddress =
                    string.Equals(model.FromWarehouse ?? "", model.ToWarehouse ?? "", StringComparison.Ordinal)
                    && string.Equals(model.FromLocation ?? "", model.ToLocation ?? "", StringComparison.Ordinal)
                    && string.Equals(model.FromBatchNo ?? "", model.ToBatchNo ?? "", StringComparison.Ordinal);

                if (sameAddress)
                {
                    Logger.LogWarning("[{TraceId}] 转移失败：源和目标相同，物料 {MaterialCode}，操作员 {Operator}", traceId, model.MaterialCode, operatorId);
                    return (false, GetText("Transfer", "FromAndToSame"), null);
                }

                // ===== 执行阶段 =====

                authService = BusinessServiceFactory.CreateAuthService();
                var transferService = new EpicorTransferService(
                    authService,
                    authService.Company
                );

                var result = await transferService.TransferInventoryAsync(
                    partNum: model.MaterialCode,
                    quantity: model.Quantity,
                    fromWarehouse: model.FromWarehouse!,
                    fromBinNum: model.FromLocation!,
                    fromLotNum: model.FromBatchNo!,
                    toWarehouse: model.ToWarehouse!,
                    toBinNum: model.ToLocation!,
                    toLotNum: model.ToBatchNo!,
                    isToBatchNo: batchExists,
                    remark: model.Remark
                );

                // 检查 Epicor API 是否返回错误（如负库存等业务校验失败）
                if (authService.HasApiError)
                {
                    Logger.LogWarning("[{TraceId}] 转移失败（Epicor API 错误）：物料 {MaterialCode}，错误：{Error}，原因 {Remark}",
                        traceId, model.MaterialCode, authService.LastApiError, remarkText);
                    DocumentOperationLogger.Log("库存转移",
                        $"物料{model.MaterialCode}/数量{model.Quantity}/自{model.FromWarehouse}-{model.FromLocation}至{model.ToWarehouse}-{model.ToLocation}{remarkSuffix}",
                        operatorId, success: false);
                    // 幂等区分两类失败：
                    // - 确定性失败：响应体中能提取到 BO 错误信息（顶层 ErrorMessage，或嵌套 returnObj.ErrorMessage，
                    //   与 CheckEpicorApiError 判定口径一致），说明 Epicor 明确拒绝、事务未提交，
                    //   不落"未知"标记，操作员修正数据后用同一 X-Request-ID 重试应允许重执行；
                    // - 真未知失败：仅有 HasApiError 但取不到 BO 错误体（传输/超时类），本次可能已在
                    //   Epicor 侧转移，仍落"未知"标记拦截重放，防止重复转移。
                    string? boError = result?["ErrorMessage"]?.ToString();
                    if (string.IsNullOrWhiteSpace(boError) && result?["returnObj"] is Newtonsoft.Json.Linq.JObject retObj)
                        boError = retObj["ErrorMessage"]?.ToString();
                    bool deterministicFailure = !string.IsNullOrWhiteSpace(boError);
                    if (!deterministicFailure)
                    {
                        MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);
                    }
                    return (false, authService.LastApiError, null);
                }

                // 成功：写入幂等性缓存（Epicor 已转移，缓存失败不应掩盖成功结果）
                try
                {
                    MarkRequestProcessed(requestId);
                }
                catch (Exception markEx)
                {
                    Logger.LogWarning(markEx, "[{TraceId}] MarkRequestProcessed 失败（Epicor 转移已成功，仅幂等标记失败）：RequestId {RequestId}",
                        traceId, requestId);
                }

                // 单据操作明细：转移提交成功
                DocumentOperationLogger.Log("库存转移",
                    $"物料{model.MaterialCode}/数量{model.Quantity}/自{model.FromWarehouse}-{model.FromLocation}至{model.ToWarehouse}-{model.ToLocation}{remarkSuffix}",
                    operatorId, success: true);

                Logger.LogInformation("[{TraceId}] 转移成功：物料 {MaterialCode}，数量 {Quantity}，自 {From} 至 {To}，操作员 {Operator}，原因 {Remark}",
                    traceId, model.MaterialCode, model.Quantity, fromAddress, toAddress, operatorId, remarkText);

                return (true, GetText("Transfer", "TransferSuccess"), new
                {
                    PartNum = model.MaterialCode,
                    Quantity = model.Quantity,
                    FromWarehouse = model.FromWarehouse,
                    FromBatch = model.FromBatchNo,
                    FromLocation = model.FromLocation,
                    ToWarehouse = model.ToWarehouse,
                    ToBatch = model.ToBatchNo,
                    ToLocation = model.ToLocation,
                    Operator = operatorId,
                    TransferTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{TraceId}] 转移过程异常：物料 {MaterialCode}，原因 {Remark}", traceId, model.MaterialCode, remarkText);

                // 单据操作明细：转移提交异常按失败记录
                DocumentOperationLogger.Log("库存转移",
                    $"物料{model.MaterialCode}/数量{model.Quantity}/自{model.FromWarehouse}-{model.FromLocation}至{model.ToWarehouse}-{model.ToLocation}{remarkSuffix}",
                    operatorId, success: false);

                // 结果未知：有可能 ERP 已经转完，只是回包没读出来，必须拦住重放；按"未知"落库
                MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);

                // ERP 的真实报错在 LastApiError 里，别一律回"操作失败"
                if (authService != null && authService.HasApiError)
                    return (false, authService.LastApiError, null);

                string? epicorMessage = ExtractEpicorBusinessError(ex);
                if (!string.IsNullOrEmpty(epicorMessage))
                    return (false, epicorMessage, null);

                return (false, GetText("Transfer", "OperationFailedRetry"), null);
            }
            finally
            {
                ReleaseLock(jobLock, lockKey, traceId);
            }
        }

        #endregion
    }
}
