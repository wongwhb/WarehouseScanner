using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WarehouseScanner.Services;
using WarehouseScannerCore.Models.DTOs;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 发料业务服务
    /// 职责：封装发料流程全部核心业务逻辑（工单 → 物料 → 库存 → 发料执行）
    /// 通用基础设施（锁 / 幂等 / TraceId / 依赖注入）由 <see cref="BaseBusinessService"/> 提供。
    /// </summary>
    public class IssueBusinessService : BaseBusinessService
    {
        // 锁键前缀：与 Receipt 等业务服务在共享锁字典中相互隔离
        private const string LockPrefix = "Issue";
        private const string CacheKeyPrefix = "IssueRequest:";

        public IssueBusinessService(
            ILogger<IssueBusinessService> logger,
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

        #region 1. 工单状态验证

        public async Task<(bool success, string message)> ValidateJobAsync(string workOrderNo, int assemblySeq)
        {
            if (string.IsNullOrWhiteSpace(workOrderNo))
                return (false, GetText("MaterialIssue", "WorkOrderRequired"));

            var jobStatus = await QueryGateway.ValidateJobStatusAsync(workOrderNo);
            if (QueryGateway.HasApiError) // 优先检查 Epicor API 是否返回错误
                return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError));
            if (jobStatus.JobClosed == true || jobStatus.JobComplete == true || jobStatus.JobReleased == false)
                return (false, string.Format(GetText("MaterialIssue", "JobStatusAbnormal"), workOrderNo));

            var mtl = await QueryGateway.QueryJobMtlIssued(workOrderNo, assemblySeq, string.Empty);
            if (QueryGateway.HasApiError) // 优先检查 Epicor API 是否返回错误
                return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError));
            if (mtl == null || !mtl.Any())
                return (false, string.Format(GetText("MaterialIssue", "JobAssemblyNotFound"), workOrderNo));

            return (true, GetText("MaterialIssue", "JobValidationPassed"));
        }

        #endregion

        #region 2. 物料 + 库存验证

        public async Task<(bool success, string message, decimal unissuedQty, decimal availableQty, decimal suggestedQty, decimal requiredQty, decimal issuedQty)> ValidateInventoryAsync(
            ValidateInventoryRequest req)
        {
            if (req == null) return (false, GetText("MaterialIssue", "RequestParamsEmpty"), 0, 0, 0, 0, 0);

            // 仓库必须显式指定：为空时 BAQ 不带 WarehouseCode 过滤，会汇总全厂库存，超发拦截失效
            if (string.IsNullOrWhiteSpace(req.FromWarehouse))
                return (false, GetText("MaterialIssue", "FromWarehouseRequired"), 0, 0, 0, 0, 0);

            var jobMtlList = await QueryGateway.QueryJobMtlIssued(req.WorkOrderNo, req.AssemblySeq, req.MaterialCode);
            if (QueryGateway.HasApiError) // 优先检查 Epicor API 是否返回错误
                return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError), 0, 0, 0, 0, 0);
            var jobMtl = jobMtlList?.FirstOrDefault();
            if (jobMtl == null)
                return (false, string.Format(GetText("MaterialIssue", "MaterialNotFound"), req.WorkOrderNo, req.MaterialCode), 0, 0, 0, 0, 0);

            // 工单物料口径：需求数量 = 工单需求；已发数量 = 累计已发；未发数量 = 需求 - 已发
            decimal requiredQty = jobMtl.RequiredQty;
            decimal issuedQty = jobMtl.QtyPreviouslyIssued;

            if (!string.Equals(jobMtl.DspIUM, req.Unit, StringComparison.OrdinalIgnoreCase))
                return (false, string.Format(GetText("MaterialIssue", "UnitMismatch"), req.Unit, jobMtl.DspIUM), 0, 0, 0, requiredQty, issuedQty);

            decimal unissuedQty = jobMtl.RemainingQty;
            if (unissuedQty <= 0)
                return (false, string.Format(GetText("MaterialIssue", "MaterialFullyIssued"), req.MaterialCode), 0, 0, 0, requiredQty, issuedQty);

            // 按 物料代码 + 批号 + 仓库 + 库位 四个维度查询库存 BAQ（Inve_view）；空值维度不参与过滤
            var inventoryList = await QueryGateway.GetInventoryDetailByInveViewBAQAsync(
                req.MaterialCode, req.FromWarehouse, req.Location, req.BatchNo);
            if (QueryGateway.HasApiError) // 优先检查 Epicor API 是否返回错误
                return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError), unissuedQty, 0, 0, requiredQty, issuedQty);
            if (inventoryList == null || !inventoryList.Any())
                return (false, GetText("MaterialIssue", "NoInventoryInWhseBinLot"), unissuedQty, 0, 0, requiredQty, issuedQty);

            // BAQ 可能返回多行（同维度多条 PartBin），必须汇总在手数量，禁止取首行。
            // Inve_view 不返回已分配数量，库存口径 = Σ QtyOnHand（== AvailableQty）。
            decimal availableQty = inventoryList.Sum(x => x.QtyOnHand);
            if (availableQty <= 0)
                return (false, string.Format(GetText("MaterialIssue", "InsufficientInventory"), req.MaterialCode), unissuedQty, 0, 0, requiredQty, issuedQty);

            // 建议数量 = min(库存数量, 扫码数量, 未发数量)
            decimal suggestedQty = Math.Min(Math.Min(availableQty, req.Quantity), unissuedQty);

            // 扫码数量超过库存数量时，不清空表单，弹出信息后允许修改数量
            if (req.Quantity > 0 && req.Quantity > availableQty)
            {
                return (true, string.Format(GetText("MaterialIssue", "QuantityAdjustedToSuggested"), req.Quantity, availableQty, suggestedQty), unissuedQty, availableQty, suggestedQty, requiredQty, issuedQty);
            }

            return (true, GetText("MaterialIssue", "InventoryValidationPassed"), unissuedQty, availableQty, suggestedQty, requiredQty, issuedQty);
        }

        #endregion

        #region 3. 执行发料（核心方法）

        /// <summary>
        /// 执行发料（消除重复 API 调用）
        /// 工单验证、物料查询、库存查询各只调用一次，结果向下传递
        /// </summary>
        public async Task<(bool success, string message, IssueMaterialResult? data)> IssueMaterialAsync(
            IssueMaterialRequest req, string operatorId, string? requestId = null)
        {
            string traceId = GetTraceId();

            if (req == null)
            {
                Logger.LogWarning("[{TraceId}] 发料失败：请求参数为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("MaterialIssue", "RequestParamsEmpty"), null);
            }
            if (string.IsNullOrWhiteSpace(req.WorkOrderNo))
            {
                Logger.LogWarning("[{TraceId}] 发料失败：工单号为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("MaterialIssue", "WorkOrderRequired"), null);
            }
            if (req.AssemblySeq < 0)
            {
                Logger.LogWarning("[{TraceId}] 发料失败：装配序号无效 {AssemblySeq}，工单 {WorkOrderNo}，操作员 {Operator}", traceId, req.AssemblySeq, req.WorkOrderNo, operatorId);
                return (false, GetText("MaterialIssue", "AssemblySeqInvalid"), null);
            }
            if (string.IsNullOrWhiteSpace(req.MaterialCode))
            {
                Logger.LogWarning("[{TraceId}] 发料失败：物料代码为空，工单 {WorkOrderNo}，操作员 {Operator}", traceId, req.WorkOrderNo, operatorId);
                return (false, GetText("MaterialIssue", "MaterialCodeRequiredMsg"), null);
            }
            if (req.Quantity <= 0)
            {
                Logger.LogWarning("[{TraceId}] 发料失败：数量无效 {Quantity}，工单 {WorkOrderNo}，操作员 {Operator}", traceId, req.Quantity, req.WorkOrderNo, operatorId);
                return (false, GetText("MaterialIssue", "QuantityMustBePositive"), null);
            }
            // 仓库必须显式指定：为空时 BAQ 不带 WarehouseCode 过滤，会汇总全厂库存，超发拦截失效
            if (string.IsNullOrWhiteSpace(req.FromWarehouse))
            {
                Logger.LogWarning("[{TraceId}] 发料失败：仓库为空，工单 {WorkOrderNo}，物料 {MaterialCode}，操作员 {Operator}", traceId, req.WorkOrderNo, req.MaterialCode, operatorId);
                return (false, GetText("MaterialIssue", "FromWarehouseRequired"), null);
            }

            Logger.LogInformation("[{TraceId}] 发料开始：工单 {WorkOrderNo}，装配 {AssemblySeq}，物料 {MaterialCode}，数量 {Quantity}，操作员 {Operator}",
                traceId, req.WorkOrderNo, req.AssemblySeq, req.MaterialCode, req.Quantity, operatorId);

            // 幂等性前置检查：命中时必须区分"上次成功"与"结果未知"，否则会把失败误报成成功
            var previousResult = TryGetRequestResult(requestId);
            if (previousResult != null)
            {
                bool previousSucceeded = previousResult == IBusinessLockProvider.RequestResultSuccess;
                Logger.LogInformation("[{TraceId}] 幂等请求已处理: RequestId={RequestId}，结果={Result}", traceId, requestId, previousResult);
                return (previousSucceeded,
                        previousSucceeded ? GetText("MaterialIssue", "RequestAlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                        null);
            }

            // 工单级并发锁（针对特定物料，允许多用户操作同一工单的不同物料）
            string lockKey = $"{req.WorkOrderNo}_{req.AssemblySeq}_{req.MaterialCode}";
            var jobLock = await TryAcquireLockAsync(lockKey, traceId, operatorId);
            if (jobLock == null)
            {
                Logger.LogWarning("[{TraceId}] 发料失败：获取锁超时，工单 {WorkOrderNo}，物料 {MaterialCode}，操作员 {Operator}", traceId, req.WorkOrderNo, req.MaterialCode, operatorId);
                return (false, GetText("MaterialIssue", "SystemBusy"), null);
            }

            try
            {
                // 双重幂等性检查：命中同样必须区分成功与结果未知
                var lockedResult = TryGetRequestResult(requestId);
                if (lockedResult != null)
                {
                    bool lockedSucceeded = lockedResult == IBusinessLockProvider.RequestResultSuccess;
                    Logger.LogInformation("[{TraceId}] 幂等请求已处理（锁内二次检查）: RequestId={RequestId}，结果={Result}", traceId, requestId, lockedResult);
                    return (lockedSucceeded,
                            lockedSucceeded ? GetText("MaterialIssue", "RequestAlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                            null);
                }

                // ===== 查询阶段：每个 API 只调用一次 =====

                // 1. 工单状态验证（1 次 API）
                var jobStatus = await QueryGateway.ValidateJobStatusAsync(req.WorkOrderNo);
                if (QueryGateway.HasApiError)
                {
                    Logger.LogError("[{TraceId}] 发料失败：工单状态验证 API 错误，工单 {WorkOrderNo}，错误 {Error}", traceId, req.WorkOrderNo, QueryGateway.LastApiError);
                    return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError), null);
                }

                if (jobStatus.JobClosed == true || jobStatus.JobComplete == true || jobStatus.JobReleased == false)
                {
                    Logger.LogWarning("[{TraceId}] 发料失败：工单 {WorkOrderNo} 状态异常（已关闭、已完工或未发布），操作员 {Operator}", traceId, req.WorkOrderNo, operatorId);
                    return (false, string.Format(GetText("MaterialIssue", "JobStatusAbnormal"), req.WorkOrderNo), null);
                }

                // 2. 工单物料查询（1 次 API）
                var mtlList = await QueryGateway.QueryJobMtlIssued(req.WorkOrderNo, req.AssemblySeq, req.MaterialCode);
                if (QueryGateway.HasApiError)
                {
                    Logger.LogError("[{TraceId}] 发料失败：工单物料查询 API 错误，工单 {WorkOrderNo}，物料 {MaterialCode}，错误 {Error}",
                        traceId, req.WorkOrderNo, req.MaterialCode, QueryGateway.LastApiError);
                    return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError), null);
                }
                if (mtlList == null || !mtlList.Any())
                {
                    Logger.LogWarning("[{TraceId}] 发料失败：未找到工单 {WorkOrderNo} 的物料 {MaterialCode}，操作员 {Operator}", traceId, req.WorkOrderNo, req.MaterialCode, operatorId);
                    return (false, string.Format(GetText("MaterialIssue", "MaterialNotFound"), req.WorkOrderNo, req.MaterialCode), null);
                }

                var mtlInfo = mtlList.First();

                // 3. 单位校验
                if (!string.Equals(mtlInfo.DspIUM, req.Unit, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning("[{TraceId}] 发料失败：单位不匹配，请求 {ReqUnit}，系统 {SysUnit}，工单 {WorkOrderNo}，物料 {MaterialCode}",
                        traceId, req.Unit, mtlInfo.DspIUM, req.WorkOrderNo, req.MaterialCode);
                    return (false, string.Format(GetText("MaterialIssue", "UnitMismatch"), req.Unit, mtlInfo.DspIUM), null);
                }

                // 4. 未发数量
                decimal unissuedQty = mtlInfo.RemainingQty;
                if (unissuedQty <= 0)
                {
                    Logger.LogWarning("[{TraceId}] 发料失败：物料 {MaterialCode} 已全部发完，工单 {WorkOrderNo}，操作员 {Operator}", traceId, req.MaterialCode, req.WorkOrderNo, operatorId);
                    return (false, string.Format(GetText("MaterialIssue", "MaterialFullyIssued"), req.MaterialCode), null);
                }

                // 5. 库存查询（1 次 API）：按 物料代码 + 批号 + 仓库 + 库位 查库存 BAQ（Inve_view），与 ValidateInventoryAsync 同口径
                var inventoryList = await QueryGateway.GetInventoryDetailByInveViewBAQAsync(
                    req.MaterialCode, req.FromWarehouse, req.Location, req.BatchNo);
                if (QueryGateway.HasApiError)
                {
                    Logger.LogError("[{TraceId}] 发料失败：库存查询 API 错误，物料 {MaterialCode}，仓库 {Warehouse}，错误 {Error}",
                        traceId, req.MaterialCode, req.FromWarehouse, QueryGateway.LastApiError);
                    return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError), null);
                }
                if (inventoryList == null || !inventoryList.Any())
                {
                    Logger.LogWarning("[{TraceId}] 发料失败：未找到库存记录，物料 {MaterialCode}，仓库 {Warehouse}，库位 {Bin}，批号 {Lot}，操作员 {Operator}",
                        traceId, req.MaterialCode, req.FromWarehouse, req.Location, req.BatchNo, operatorId);
                    return (false, GetText("MaterialIssue", "NoInventoryInWhseBinLot"), null);
                }

                // 6. 数量上限校验（与 ValidateInventoryAsync 保持一致：汇总在手数量，拒绝超量而非静默截断）
                decimal availableQty = inventoryList.Sum(x => x.QtyOnHand);
                if (availableQty <= 0)
                {
                    Logger.LogWarning("[{TraceId}] 发料失败：物料 {MaterialCode} 可用库存不足，工单 {WorkOrderNo}，操作员 {Operator}",
                        traceId, req.MaterialCode, req.WorkOrderNo, operatorId);
                    return (false, string.Format(GetText("MaterialIssue", "InsufficientInventory"), req.MaterialCode), null);
                }

                if (req.Quantity > unissuedQty)
                {
                    Logger.LogWarning("[{TraceId}] 发料失败：请求数量 {ReqQty} 超过未发数量 {Unissued}，工单 {WorkOrderNo}，物料 {MaterialCode}，操作员 {Operator}",
                        traceId, req.Quantity, unissuedQty, req.WorkOrderNo, req.MaterialCode, operatorId);
                    return (false, string.Format(GetText("MaterialIssue", "QuantityExceedsUnissued"), req.Quantity, unissuedQty), null);
                }

                if (req.Quantity > availableQty)
                {
                    Logger.LogWarning("[{TraceId}] 发料失败：请求数量 {ReqQty} 超过可用库存 {Available}，工单 {WorkOrderNo}，物料 {MaterialCode}，操作员 {Operator}",
                        traceId, req.Quantity, availableQty, req.WorkOrderNo, req.MaterialCode, operatorId);
                    return (false, string.Format(GetText("MaterialIssue", "QuantityExceedsInventory"), req.Quantity, availableQty), null);
                }

                // 至此：req.Quantity 同时 <= 未发数量 与 可用库存，无需截断
                decimal issueQty = req.Quantity;

                // ===== 执行阶段 =====
                // 注：partNum 传 string.Empty 是已知行为，Epicor BO 会根据 jobNum + assemblySeq
                // 自动回填 JobHeadPartNum；MatNum 才是真正要发料的物料编号。
                var issueService = await BusinessServiceFactory.CreateIssueServiceAsync();
                var result = await issueService.IssueMaterialAsync(
                    jobNum: req.WorkOrderNo,
                    assemblySeq: req.AssemblySeq,
                    partNum: string.Empty,
                    MatNum: req.MaterialCode,
                    whseCode: req.FromWarehouse ?? string.Empty,
                    binNum: string.IsNullOrEmpty(req.Location) ? string.Empty : req.Location,
                    lotNumber: string.IsNullOrEmpty(req.BatchNo) ? string.Empty : req.BatchNo,
                    quantity: (double)issueQty,
                    toJobSeq: mtlInfo.MtlSeq);

                // 统一检查 Epicor API 错误（HTTP 状态码允许 2xx 全段，避免误判 201/202）
                var httpStatusStr = result?["HttpStatus"]?.ToString();
                bool isHttpFail = result != null
                    && result.ContainsKey("HttpStatus")
                    && (!int.TryParse(httpStatusStr, out int httpStatus) || httpStatus < 200 || httpStatus >= 300);
                if (issueService.HasApiError || isHttpFail)
                {
                    string errMsg = issueService.LastApiError ?? (result?["ErrorMessage"]?.ToString() ?? GetText("MaterialIssue", "IssueApiCallFailed"));
                    Logger.LogError("[{TraceId}] 发料失败：工单 {WorkOrderNo}，物料 {MaterialCode}，错误: {Error}",
                        traceId, req.WorkOrderNo, req.MaterialCode, errMsg);
                    DocumentOperationLogger.Log("发料管理", $"工单{req.WorkOrderNo}/装配{req.AssemblySeq}/物料{req.MaterialCode}/数量{issueQty}", operatorId, success: false);
                    // 幂等区分两类失败：
                    // - 确定性失败：响应体中能提取到 BO 错误信息（顶层 ErrorMessage，或嵌套 returnObj.ErrorMessage，
                    //   与 CheckEpicorApiError 判定口径一致），说明 Epicor 明确拒绝、事务未提交，
                    //   不落"未知"标记，操作员修正数据后用同一 X-Request-ID 重试应允许重执行；
                    // - 真未知失败：仅有 HasApiError/HTTP 失败但取不到 BO 错误体（传输/超时类），
                    //   本次可能已在 Epicor 侧发料，仍落"未知"标记拦截重放，防止重复发料。
                    string? boError = result?["ErrorMessage"]?.ToString();
                    if (string.IsNullOrWhiteSpace(boError) && result?["returnObj"] is Newtonsoft.Json.Linq.JObject retObj)
                        boError = retObj["ErrorMessage"]?.ToString();
                    bool deterministicFailure = !string.IsNullOrWhiteSpace(boError);
                    if (!deterministicFailure)
                    {
                        MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);
                    }
                    return (false, errMsg, null);
                }

                // 成功：写入幂等性缓存（Epicor 已发料，缓存失败不应掩盖成功结果）
                try
                {
                    MarkRequestProcessed(requestId);
                }
                catch (Exception markEx)
                {
                    Logger.LogWarning(markEx, "[{TraceId}] MarkRequestProcessed 失败（Epicor 发料已成功，仅幂等标记失败）：RequestId {RequestId}",
                        traceId, requestId);
                }

                // 单据操作明细：发料提交成功
                DocumentOperationLogger.Log("发料管理", $"工单{req.WorkOrderNo}/装配{req.AssemblySeq}/物料{req.MaterialCode}/数量{issueQty}", operatorId, success: true);

                Logger.LogInformation(
                    "[{TraceId}] 发料成功：工单 {WorkOrderNo}，装配 {AssemblySeq}，物料 {MaterialCode}，数量 {Quantity}，操作员 {Operator}",
                    traceId, req.WorkOrderNo, req.AssemblySeq, req.MaterialCode, issueQty, operatorId);

                return (true, GetText("MaterialIssue", "SubmitSuccess"), new IssueMaterialResult
                {
                    WorkOrderNo = req.WorkOrderNo,
                    AssemblySeq = req.AssemblySeq,
                    MaterialCode = req.MaterialCode,
                    Quantity = issueQty,
                    BatchNo = req.BatchNo,
                    Location = req.Location,
                    Operator = operatorId,
                    IssueTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{TraceId}] 发料过程异常：工单 {WorkOrderNo}", traceId, req.WorkOrderNo);

                // 单据操作明细：发料提交异常按失败记录
                DocumentOperationLogger.Log("发料管理", $"工单{req.WorkOrderNo}/装配{req.AssemblySeq}/物料{req.MaterialCode}/数量{req.Quantity}", operatorId, success: false);

                // 幂等：异常同样落标记（异常可能发生在"Epicor 已发料、仅响应解析失败"之后），
                // 防止客户端用同一 X-Request-ID 重放造成重复发料；按"未知"落库。
                MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);

                string? epicorMessage = ExtractEpicorBusinessError(ex);
                if (!string.IsNullOrEmpty(epicorMessage))
                    return (false, epicorMessage, null);

                return (false, GetText("MaterialIssue", "OperationFailed"), null);
            }
            finally
            {
                ReleaseLock(jobLock, lockKey, traceId);
            }
        }

        #endregion

        #region 4. 查询工单信息

        public async Task<(bool success, string message, JobStatusInfo? data)> GetJobInfoAsync(string workOrderNo)
        {
            if (string.IsNullOrWhiteSpace(workOrderNo)) return (false, GetText("MaterialIssue", "WorkOrderRequired"), null);
            var info = await QueryGateway.ValidateJobStatusAsync(workOrderNo);
            if (QueryGateway.HasApiError)
                return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError), null);
            return (true, string.Empty, info);
        }

        #endregion

        #region 5. 查询物料库存

        public async Task<(bool success, string message, List<PartInventoryInfo> data)> GetMaterialInfoAsync(string materialCode)
        {
            if (string.IsNullOrWhiteSpace(materialCode))
                return (false, GetText("MaterialIssue", "MaterialCodeRequiredMsg"), new List<PartInventoryInfo>());
            var data = await QueryGateway.GetSimplePartBinInventory(materialCode);
            // 必须在 Epicor API 调用后检查 HasApiError，避免 API 失败时仍返回 success=true
            if (QueryGateway.HasApiError)
                return (false, string.Format(GetText("MaterialIssue", "EpicorApiFailed"), QueryGateway.LastApiError), new List<PartInventoryInfo>());
            return (true, string.Empty, data);
        }

        #endregion
    }
}
