using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WarehouseScanner.Services;
using WarehouseScannerCore.Models;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 退回业务服务
    /// 职责：封装物料退回流程全部核心业务逻辑（工单验证 → 物料查询 → 库存校验 → 退回执行）
    /// 通用基础设施（锁 / 幂等 / TraceId / 依赖注入）由 <see cref="BaseBusinessService"/> 提供。
    /// 
    /// 架构原则：Controller 只做请求调度，业务逻辑下沉到 BusinessService
    /// </summary>
    public class ReturnBusinessService : BaseBusinessService
    {
        // 锁键前缀：与 Issue/Transfer/Outbound/Receipt 等业务服务在共享锁字典中相互隔离
        private const string LockPrefix = "Return";
        private const string CacheKeyPrefix = "ReturnRequest:";

        public ReturnBusinessService(
            ILogger<ReturnBusinessService> logger,
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

        #region 3. 执行退回（核心方法）

        /// <summary>
        /// 执行物料退回（消除重复 API 调用）
        /// 工单验证、物料查询、库存查询各只调用一次，结果向下传递
        /// </summary>
        /// <param name="model">退回请求模型</param>
        /// <param name="operatorId">操作员ID</param>
        /// <param name="requestId">幂等性请求ID（可选）</param>
        /// <returns>(success, message, data)</returns>
        /// <remarks>
        /// - 幂等性：基于 requestId 缓存 60 分钟，前置 + 双重检查
        /// - 并发控制：基于工单号+装配序号的复合锁，30秒超时
        /// - 性能：每个 API 只调用一次（工单物料查询、库位验证）
        /// </remarks>
        public async Task<(bool success, string message, object? data)> ReturnMaterialAsync(
            MaterialReturnModel model, string operatorId, string? requestId = null)
        {
            string traceId = GetTraceId();

            if (model == null)
            {
                Logger.LogWarning("[{TraceId}] 退回失败：请求参数为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("MaterialReturn", "RequestNull"), null);
            }
            if (string.IsNullOrWhiteSpace(model.WorkOrderNo))
            {
                Logger.LogWarning("[{TraceId}] 退回失败：工单号为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("MaterialReturn", "WorkOrderRequired"), null);
            }
            if (model.Quantity <= 0)
            {
                Logger.LogWarning("[{TraceId}] 退回失败：数量无效 {Quantity}，工单 {WorkOrderNo}，操作员 {Operator}", traceId, model.Quantity, model.WorkOrderNo, operatorId);
                return (false, GetText("MaterialReturn", "QuantityRequired"), null);
            }

            Logger.LogInformation("[{TraceId}] 退回开始：工单 {WorkOrderNo}，装配 {AssemblySeq}，物料 {MaterialCode}，数量 {Quantity}，操作员 {Operator}",
                traceId, model.WorkOrderNo, model.AssemblySeq, model.MaterialCode, model.Quantity, operatorId);

            // 幂等性前置检查：命中时必须区分"上次成功"与"结果未知"，否则会把失败误报成成功
            var previousResult = TryGetRequestResult(requestId);
            if (previousResult != null)
            {
                bool previousSucceeded = previousResult == IBusinessLockProvider.RequestResultSuccess;
                Logger.LogInformation("[{TraceId}] 幂等请求已处理: RequestId={RequestId}，结果={Result}", traceId, requestId, previousResult);
                return (previousSucceeded,
                        previousSucceeded ? GetText("MaterialReturn", "AlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                        null);
            }

            // 工单级并发锁（针对特定物料，允许多用户操作同一工单的不同物料）
            string lockKey = $"{model.WorkOrderNo}_{model.AssemblySeq}_{model.MaterialCode}";
            var jobLock = await TryAcquireLockAsync(lockKey, traceId, operatorId);
            if (jobLock == null)
            {
                Logger.LogWarning("[{TraceId}] 退回失败：获取锁超时，工单 {WorkOrderNo}，物料 {MaterialCode}，操作员 {Operator}", traceId, model.WorkOrderNo, model.MaterialCode, operatorId);
                return (false, GetText("MaterialReturn", "SystemBusy"), null);
            }

            // 放 try 外面，catch 里才拿得到它读 ERP 报了什么错
            EpicorReturnService? returnService = null;

            try
            {
                // 双重幂等性检查：命中时同样按结果码区分应答
                var lockedResult = TryGetRequestResult(requestId);
                if (lockedResult != null)
                {
                    bool lockedSucceeded = lockedResult == IBusinessLockProvider.RequestResultSuccess;
                    Logger.LogInformation("[{TraceId}] 幂等请求已处理（锁内二次检查）: RequestId={RequestId}，结果={Result}", traceId, requestId, lockedResult);
                    return (lockedSucceeded,
                            lockedSucceeded ? GetText("MaterialReturn", "AlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                            null);
                }

                // ===== 查询阶段：每个 API 只调用一次 =====

                // 1. 工单物料查询（1 次 API）
                var jobMtlList = await QueryGateway.QueryJobMtlIssued(model.WorkOrderNo, model.AssemblySeq, "");
                if (QueryGateway.HasApiError)
                {
                    Logger.LogError("[{TraceId}] 退回失败：Epicor API 调用失败，工单 {WorkOrderNo}，错误: {Error}", traceId, model.WorkOrderNo, QueryGateway.LastApiError);
                    return (false, string.Format(GetText("MaterialReturn", "EpicorApiFailed"), QueryGateway.LastApiError), null);
                }

                if (jobMtlList == null || !jobMtlList.Any())
                {
                    Logger.LogWarning("[{TraceId}] 退回失败：未找到工单 {WorkOrderNo} 的物料信息，操作员 {Operator}", traceId, model.WorkOrderNo, operatorId);
                    return (false, string.Format(GetText("MaterialReturn", "JobMaterialsNotFound"), model.WorkOrderNo), null);
                }

                // 从已查询的结果中筛选指定物料（避免第二次 API 调用）
                var jobMtl = string.IsNullOrEmpty(model.MaterialCode)
                    ? jobMtlList.FirstOrDefault()
                    : jobMtlList.FirstOrDefault(m => string.Equals(m.PartNum, model.MaterialCode, StringComparison.OrdinalIgnoreCase));

                if (jobMtl == null)
                {
                    Logger.LogWarning("[{TraceId}] 退回失败：工单 {WorkOrderNo} 未包含物料 {MaterialCode}，操作员 {Operator}", traceId, model.WorkOrderNo, model.MaterialCode, operatorId);
                    return (false, string.Format(GetText("MaterialReturn", "MaterialNotFound"), model.WorkOrderNo, model.MaterialCode), null);
                }

                // 2. 单位校验
                if (!string.Equals(jobMtl.DspIUM, model.Unit, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning("[{TraceId}] 退回失败：单位不匹配，请求 {ReqUnit}，系统 {SysUnit}，工单 {WorkOrderNo}，物料 {MaterialCode}", traceId, model.Unit, jobMtl.DspIUM, model.WorkOrderNo, model.MaterialCode);
                    return (false, string.Format(GetText("MaterialReturn", "UnitMismatch"), model.Unit, jobMtl.DspIUM), null);
                }

                // 3. 已发数量校验
                decimal issuedQty = jobMtl.QtyPreviouslyIssued;
                if (issuedQty <= 0)
                {
                    Logger.LogWarning("[{TraceId}] 退回失败：物料 {MaterialCode} 未发料，无法退回，工单 {WorkOrderNo}，操作员 {Operator}", traceId, model.MaterialCode, model.WorkOrderNo, operatorId);
                    return (false, string.Format(GetText("MaterialReturn", "NoMaterialIssued"), model.MaterialCode), null);
                }

                // #region 业务 - 退回数量超量拒绝（与预校验 ValidateInventoryAsync 一致）
                // 统一策略：请求数量超过已发数量时直接拒绝，不静默截断，避免账实不符。
                if (model.Quantity > issuedQty)
                {
                    Logger.LogWarning("[{TraceId}] 退回失败：退回数量 {ReqQty} 超过已发数量 {Issued}，工单 {WorkOrderNo}，物料 {MaterialCode}",
                        traceId, model.Quantity, issuedQty, model.WorkOrderNo, model.MaterialCode);
                    return (false, string.Format(GetText("MaterialReturn", "QuantityExceedsIssued"), model.Quantity, issuedQty), null);
                }

                decimal returnQty = model.Quantity;
                if (returnQty <= 0)
                {
                    Logger.LogWarning("[{TraceId}] 退回失败：退回数量无效，请求 {ReqQty}，已发 {Issued}，工单 {WorkOrderNo}，物料 {MaterialCode}", traceId, model.Quantity, issuedQty, model.WorkOrderNo, model.MaterialCode);
                    return (false, GetText("MaterialReturn", "InvalidReturnQuantity"), null);
                }

                // 4. 库位存在性校验（1 次 API）
                if (string.IsNullOrWhiteSpace(model.FromWarehouse))
                {
                    Logger.LogWarning("[{TraceId}] 退回失败：仓库代码为空，工单 {WorkOrderNo}，操作员 {Operator}", traceId, model.WorkOrderNo, operatorId);
                    return (false, GetText("MaterialReturn", "WarehouseRequired"), null);
                }

                // 注意：GetBinInfo 需要通过 BusinessServiceFactory 获取 authService
                var authService = BusinessServiceFactory.CreateAuthService();
                var binInfo = await authService.GetBinInfo(
                    model.FromWarehouse,
                    string.IsNullOrWhiteSpace(model.Location) ? "" : model.Location);
                if (authService.HasApiError)
                {
                    Logger.LogError("[{TraceId}] 退回失败：仓库或Bin位不存在，仓库 {Warehouse}，错误: {Error}", traceId, model.FromWarehouse, authService.LastApiError);
                    return (false, string.Format(GetText("MaterialReturn", "WarehouseOrBinNotFound"), authService.LastApiError), null);
                }


                if (binInfo == null || string.IsNullOrEmpty(binInfo.BinNum))
                {
                    Logger.LogWarning("[{TraceId}] 退回失败：库位不存在，仓库 {Warehouse}，库位 {Location}，操作员 {Operator}", traceId, model.FromWarehouse, model.Location, operatorId);
                    return (false, string.Format(GetText("MaterialReturn", "BinNotFound"), model.FromWarehouse, model.Location), null);
                }

                // ===== 执行阶段 =====
                returnService = await BusinessServiceFactory.CreateReturnServiceAsync();

                var result = await returnService.ReturnMaterialAsync(
                    jobNum: model.WorkOrderNo,
                    assemblySeq: model.AssemblySeq,
                    partNum: "",
                    MatNum: model.MaterialCode,
                    whseCode: model.FromWarehouse ?? string.Empty,
                    binNum: model.Location ?? string.Empty,
                    lotNumber: model.BatchNo ?? string.Empty,
                    quantity: (double)returnQty,
                    toJobSeq: jobMtl.MtlSeq);

                // 统一检查 Epicor API 错误
                if (returnService.HasApiError || result == null)
                {
                    string errMsg = returnService.LastApiError ?? GetText("MaterialReturn", "SubmitFailed");
                    Logger.LogError("[{TraceId}] 退回失败：工单 {WorkOrderNo}，物料 {MaterialCode}，错误: {Error}",
                        traceId, model.WorkOrderNo, model.MaterialCode, errMsg);
                    DocumentOperationLogger.Log("退料管理", $"工单{model.WorkOrderNo}/装配{model.AssemblySeq}/物料{model.MaterialCode}/数量{returnQty}", operatorId, success: false);
                    // 幂等：失败同样落标记，且标记为"结果未知"。若本次实际已在 Epicor 侧退回
                    // （仅响应解析/网络失败），客户端用同一 X-Request-ID 重放时将被拦截，
                    // 避免重复退回；同时命中应答不会把"未确认成功"回报成成功。
                    MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);
                    return (false, errMsg, null);
                }

                // 成功：写入幂等性缓存（Epicor 已退回，缓存失败不应掩盖成功结果）
                try
                {
                    MarkRequestProcessed(requestId);
                }
                catch (Exception markEx)
                {
                    Logger.LogWarning(markEx, "[{TraceId}] MarkRequestProcessed 失败（Epicor 退回已成功，仅幂等标记失败）：RequestId {RequestId}",
                        traceId, requestId);
                }

                // 单据操作明细：退料提交成功
                DocumentOperationLogger.Log("退料管理", $"工单{model.WorkOrderNo}/装配{model.AssemblySeq}/物料{model.MaterialCode}/数量{returnQty}", operatorId, success: true);

                Logger.LogInformation(
                    "[{TraceId}] 退回成功：工单 {WorkOrderNo}，装配 {AssemblySeq}，物料 {MaterialCode}，数量 {Quantity}，操作员 {Operator}",
                    traceId, model.WorkOrderNo, model.AssemblySeq, model.MaterialCode, returnQty, operatorId);

                return (true, GetText("MaterialReturn", "ReturnSuccess"), new
                {
                    WorkOrderNo = model.WorkOrderNo,
                    AssemblySeq = model.AssemblySeq,
                    MaterialCode = model.MaterialCode,
                    Quantity = returnQty,
                    BatchNo = model.BatchNo,
                    Location = model.Location,
                    Operator = operatorId,
                    ReturnTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{TraceId}] 退回过程异常：工单 {WorkOrderNo}", traceId, model.WorkOrderNo);

                // 单据操作明细：退料提交异常按失败记录
                DocumentOperationLogger.Log("退料管理", $"工单{model.WorkOrderNo}/装配{model.AssemblySeq}/物料{model.MaterialCode}/数量{model.Quantity}", operatorId, success: false);

                // 异常也记一笔并标记为"结果未知"：有可能 ERP 已经退回，只是回包没读出来，
                // 重放要拦住，且不得回报成功
                MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);

                // ERP 的真实报错在 LastApiError 里，别一律回"操作失败"
                if (returnService != null && returnService.HasApiError)
                    return (false, returnService.LastApiError, null);

                string? epicorMessage = ExtractEpicorBusinessError(ex);
                if (!string.IsNullOrEmpty(epicorMessage))
                    return (false, epicorMessage, null);

                return (false, GetText("MaterialReturn", "OperationFailedRetry"), null);
            }
            finally
            {
                ReleaseLock(jobLock, lockKey, traceId);
            }
        }

        #endregion
    }
}
