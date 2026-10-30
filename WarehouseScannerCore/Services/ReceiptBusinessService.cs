using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WarehouseScanner.Services;
using WarehouseScannerCore.Models.DTOs;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 工单入库业务服务
    /// 职责：封装工单入库流程全部核心业务逻辑
    /// 通用基础设施（锁 / 幂等 / TraceId / 依赖注入）由 <see cref="BaseBusinessService"/> 提供。
    /// </summary>
    public class ReceiptBusinessService : BaseBusinessService
    {
        // 锁键前缀：与 Issue 等业务服务在共享锁字典中相互隔离
        private const string LockPrefix = "Receipt";
        private const string CacheKeyPrefix = "ReceiptRequest:";

        public ReceiptBusinessService(
            ILogger<ReceiptBusinessService> logger,
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

        // 本地化 GetText(category, key) 已由基类 BaseBusinessService 提供

        #region 1. 工单状态验证（入库场景）

        /// <summary>
        /// 验证工单是否可以入库
        /// 校验链路（严格按顺序）：
        /// 1. 参数清洗 + API 查工单状态 → 必须存在、已发布、未关闭
        /// 2. BAQ 查数量信息（ProdQty / ReceivedQty）→ 必须查到，否则无法校验数量
        /// 3. 工单数量有效性校验（ProdQty > 0）
        /// 4. 回填默认仓库/库位/单位（失败不阻断）
        /// 5. 验证仓库、库位存在性
        /// 6. 超收提示：不做数量限制，仅产生 overReceiptWarning，允许入库
        /// </summary>
        public async Task<(bool success, string message, ReceiptJobStatusInfo? data, string? overReceiptWarning)> ValidateJobForReceiptAsync(
            string workOrderNo, int assemblySeq, decimal scanQty = 0, string materialCode = "")
        {
            // 0. 参数清洗（防御脏数据，如扫码枪尾部带出的 ']' 等）
            workOrderNo = workOrderNo?.Trim() ?? "";
            materialCode = materialCode?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(workOrderNo))
                return (false, GetText("Receipt", "JobNoRequired"), null, null);

            // 1. API 查工单头状态（JobEntrySvc）→ 必须通过才能继续
            var jobStatus = await QueryGateway.ValidateJobStatusAsync(workOrderNo);

            if (QueryGateway.HasApiError)
                return (false, string.Format(GetText("Receipt", "EpicorApiFailed"), QueryGateway.LastApiError), null, null);

            if (jobStatus == null || string.IsNullOrEmpty(jobStatus.JobNum))
                return (false, string.Format(GetText("Receipt", "JobNotExists"), workOrderNo), null, null);

            if (jobStatus.JobClosed == true)
                return (false, string.Format(GetText("Receipt", "JobClosed"), workOrderNo), null, null);

            if (jobStatus.JobComplete == true)
                return (false, string.Format(GetText("Receipt", "JobCompleted"), workOrderNo), null, null);

            if (jobStatus.JobReleased != true)
                return (false, string.Format(GetText("Receipt", "JobNotReleased"), workOrderNo), null, null);

            // 2. BAQ 查数量信息 → 必须查到，否则无法校验入库数量
            var receiptInfo = await QueryReceiptJobInfoAsync(workOrderNo, assemblySeq);

            if (receiptInfo == null)
                return (false, string.Format(GetText("Receipt", "JobReceiptInfoNotFound"), workOrderNo), null, null);

            // 3. 查询物料实际在手库存（汇总所有仓库/库位/批次的 OnhandQty），用于前端展示「库存数量」
            //    必须先于数量有效性校验执行：否则 ProdQty<=0 提前返回时 StockQty 保持 0，
            //    而前端按 prodQty 是否存在判断仍需渲染信息行，会显示错误的「库存数量 0」。
            string partNumForStock = !string.IsNullOrEmpty(materialCode) ? materialCode : receiptInfo.PartNum ?? "";
            if (!string.IsNullOrEmpty(partNumForStock))
            {
                try
                {
                    var stockList = await QueryGateway.GetSimplePartBinInventory(partNumForStock);
                    receiptInfo.StockQty = stockList?.Sum(x => x.QtyOnHand) ?? 0;
                }
                catch (Exception stockEx)
                {
                    // 库存查询失败不阻断主流程，库存数显示为 0
                    Logger.LogWarning(stockEx, "查询物料库存失败，不阻断入库流程: PartNum={PartNum}", partNumForStock);
                    receiptInfo.StockQty = 0;
                }
            }

            // 3.1 工单数量有效性校验（ProdQty <= 0 属数据完整性问题，提前拦截）
            if (receiptInfo.ProdQty <= 0)
                return (false, GetText("Receipt", "JobQtyInvalid"), receiptInfo, null);

            // 4. 回填默认仓库/库位/单位（失败不阻断主流程）
            var authService = GetAuthService();
            var receiptService = new EpicorReceiptService(authService, authService.Company ?? "", operatorId: "", plant: authService.Plant);

            string partNumForLookup = !string.IsNullOrEmpty(materialCode) ? materialCode : receiptInfo.PartNum ?? "";
            if (!string.IsNullOrEmpty(partNumForLookup))
            {
                try
                {
                    var defaultInfo = await receiptService.GetPartDefaultWarehouseAsync(partNumForLookup);
                    if (defaultInfo.HasValue)
                    {
                        // 物料在系统中不存在（404/查无数据）→ 提示用户检查后重新录入
                        if (defaultInfo.Value.partNotFound)
                            return (false, string.Format(GetText("Receipt", "PartNotExists"), partNumForLookup), receiptInfo, null);

                        if (!string.IsNullOrEmpty(defaultInfo.Value.warehouseCode))
                        {
                            receiptInfo.WarehouseCode = defaultInfo.Value.warehouseCode;
                            string binNum = await receiptService.GetWhseBinDefaultAsync(defaultInfo.Value.warehouseCode);
                            if (!string.IsNullOrEmpty(binNum))
                                receiptInfo.BinNum = binNum;
                        }
                        if (!string.IsNullOrEmpty(defaultInfo.Value.unit))
                            receiptInfo.Unit = defaultInfo.Value.unit;
                    }
                    else if (authService.HasApiError)
                    {
                        // 接口挂了也返回 null，别当成"物料不存在"，不然前端会把已填的字段清掉
                        return (false, string.Format(GetText("Receipt", "EpicorApiFailed"), authService.LastApiError), receiptInfo, null);
                    }
                    else
                    {
                        return (false, string.Format(GetText("Receipt", "PartNotExists"), partNumForLookup), receiptInfo, null);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "获取默认仓库/库位失败，不影响主流程");
                }
            }

            // 5. 验证仓库、库位存在性（失败返回，不吞异常）
            //    注意：收货数量校验已移至步骤 6，确保超限时返回的 data 包含完整仓库/库位/单位信息
            if (!string.IsNullOrEmpty(receiptInfo.WarehouseCode))
            {
                try
                {
                    var (whseExists, _) = await receiptService.CheckWarehouseExistsAsync(receiptInfo.WarehouseCode);
                    if (!whseExists)
                        return (false, string.Format(GetText("Receipt", "WarehouseNotExists"), receiptInfo.WarehouseCode), receiptInfo, null);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "仓库验证异常");
                }
            }

            if (!string.IsNullOrEmpty(receiptInfo.BinNum))
            {
                try
                {
                    var (binExists, _) = await receiptService.CheckBinExistsAsync(receiptInfo.WarehouseCode, receiptInfo.BinNum);
                    if (!binExists)
                        return (false, string.Format(GetText("Receipt", "BinNotExists"), receiptInfo.BinNum), receiptInfo, null);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "库位验证异常");
                }
            }

            // #region 业务 - 超收提示（不限制超收数量，仅提示，允许入库）
            // 超收规则（业务确认 2026-10-17）：取消数量上限，
            // 超收仅通过 overReceiptWarning 透出提示，工单验证照常通过，提交不被阻断。
            string? overReceiptWarning = null;
            if (scanQty > 0 && scanQty + receiptInfo.ReceivedQty > receiptInfo.ProdQty)
            {
                overReceiptWarning = string.Format(GetText("Receipt", "OverReceiptWarning"), scanQty, receiptInfo.ReceivedQty, receiptInfo.ProdQty);
                Logger.LogWarning("超收提示（允许入库）：本次 {ScanQty} + 已收 {ReceivedQty} 超过工单数量 {ProdQty}",
                    scanQty, receiptInfo.ReceivedQty, receiptInfo.ProdQty);
            }

            return (true, GetText("Receipt", "JobValidated"), receiptInfo, overReceiptWarning);
        }

        #endregion

        #region 2. 查询工单入库信息

        /// <summary>
        /// 查询工单入库信息（通过 Job_view BAQ）
        /// BAQ 参数别名：@workOrderNo, @AssemblySeq
        /// </summary>
        private async Task<ReceiptJobStatusInfo?> QueryReceiptJobInfoAsync(string workOrderNo, int assemblySeq = 0)
        {
            try
            {
                var authService = GetAuthService();
                var baqId = "Job_view";

                var parameters = new List<object>
                {
                    new { ParameterID = "workOrderNo", ParameterValue = workOrderNo, ValueType = "string", IsEmpty = false }
                };

                if (assemblySeq > 0)
                    parameters.Add(new { ParameterID = "AssemblySeq", ParameterValue = assemblySeq.ToString(), ValueType = "int", IsEmpty = false });

                // 多站点隔离：配置了 Plant 时收敛 BAQ 数据可见性到当前工厂
                if (!string.IsNullOrEmpty(authService.Plant))
                    parameters.Add(new { ParameterID = "Plant", ParameterValue = authService.Plant, ValueType = "string", IsEmpty = false });

                string json = JsonConvert.SerializeObject(new
                {
                    queryID = baqId,
                    executionParams = new
                    {
                        ExecutionParameter = parameters.ToArray()
                    }
                });

                string body = await authService.PostBoJsonAsync(
                    "Ice.BO.DynamicQuerySvc/ExecuteByID", json);

                if (string.IsNullOrEmpty(body))
                {
                    Logger.LogWarning("QueryReceiptJobInfoAsync 返回空响应: JobNum={JobNum}", workOrderNo);
                    return null;
                }

                // 接口报错就返回 null。原来只查 HttpStatus/ErrorMessage 两个字串，RFC9110 的错误体漏掉了
                if (authService.HasApiError)
                {
                    Logger.LogError("BAQ查询返回错误: JobNum={JobNum}, Error={Error}", workOrderNo, authService.LastApiError);
                    return null;
                }

                var jObj = JObject.Parse(body);
                var arr = jObj["returnObj"]?["Results"] as JArray;
                if (arr == null || !arr.Any())
                {
                    Logger.LogWarning("BAQ查询无结果: JobNum={JobNum}, AssemblySeq={AssemblySeq}", workOrderNo, assemblySeq);
                    return null;
                }

                var first = arr.First() as JObject;
                if (first == null) return null;

                string jobNum = first["JobNum"]?.Value<string>() ?? workOrderNo;
                string partNum = first["PartNum"]?.Value<string>() ?? string.Empty;

                // Newtonsoft 的 Value<T>() 内部用 InvariantCulture 做 Convert.ChangeType，
                // 同时处理 Boolean/String 两种布尔形式和 Float/Integer/String 三种数字形式，
                // 一行顶十行，不存在 CultureInfo 问题
                bool jobFirm = (first["JobHead_JobFirm"] ?? first["JobFirm"])?.Value<bool?>() ?? false;
                bool jobClosed = (first["JobHead_JobClosed"] ?? first["JobClosed"])?.Value<bool?>() ?? false;
                decimal prodQty = (first["JobProd_ProdQty"] ?? first["ProdQty"])?.Value<decimal?>() ?? 0;
                decimal receivedQty = (first["Calculated_total_tranQty"] ?? first["ReceivedQty"])?.Value<decimal?>() ?? 0;

                var result = new ReceiptJobStatusInfo
                {
                    JobNum = jobNum,
                    PartNum = partNum,
                    JobFirm = jobFirm,
                    JobClosed = jobClosed,
                    ProdQty = prodQty,
                    ReceivedQty = receivedQty
                };

                Logger.LogInformation("BAQ查询成功: JobNum={JobNum}, AssemblySeq={AssemblySeq}, PartNum={PartNum}, ProdQty={ProdQty}, ReceivedQty={ReceivedQty}",
                    result.JobNum, assemblySeq, result.PartNum, result.ProdQty, result.ReceivedQty);

                return result;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "查询工单入库信息异常: JobNum={JobNum}, AssemblySeq={AssemblySeq}", workOrderNo, assemblySeq);
                return null;
            }
        }

        private EpicorAuthService GetAuthService()
        {
            return BusinessServiceFactory.CreateAuthService();
        }

        #endregion

        #region 4. 执行入库（核心方法）

        /// <summary>
        /// 执行工单入库操作（单阶段，业务允许超收）
        /// pcMessage 仅作为警告信息返回，不阻断入库流程。
        /// </summary>
        public async Task<(bool success, string message, ReceiveMfgPartResult? data, string? warningMessage)> ReceiveMfgPartAsync(
            ReceiveMfgPartRequest req, string operatorId, string? requestId = null)
        {
            string traceId = GetTraceId();

            if (req == null)
            {
                Logger.LogWarning("[{TraceId}] 入库失败：请求参数为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("Receipt", "RequestParamEmpty"), null, null);
            }
            if (string.IsNullOrWhiteSpace(req.WorkOrderNo))
            {
                Logger.LogWarning("[{TraceId}] 入库失败：工单号为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("Receipt", "JobNoRequired"), null, null);
            }
            // 数量 ≤0 防御，避免负值/零值绕过业务规则直接入库
            if (req.Quantity <= 0)
            {
                Logger.LogWarning("[{TraceId}] 入库失败：数量无效 {Quantity}，工单 {WorkOrderNo}，操作员 {Operator}", traceId, req.Quantity, req.WorkOrderNo, operatorId);
                return (false, GetText("Receipt", "QuantityMustBePositive"), null, null);
            }

            Logger.LogInformation("[{TraceId}] 入库开始：工单 {WorkOrderNo}，装配 {AssemblySeq}，物料 {MaterialCode}，数量 {Quantity}，操作员 {Operator}",
                traceId, req.WorkOrderNo, req.AssemblySeq, req.MaterialCode, req.Quantity, operatorId);

            // 幂等性前置检查：命中时必须区分"上次成功"与"结果未知"，否则会把失败误报成成功
            var previousResult = TryGetRequestResult(requestId);
            if (previousResult != null)
            {
                bool previousSucceeded = previousResult == IBusinessLockProvider.RequestResultSuccess;
                Logger.LogInformation("[{TraceId}] 幂等请求已处理: RequestId={RequestId}，结果={Result}", traceId, requestId, previousResult);
                return (previousSucceeded,
                        previousSucceeded ? GetText("Receipt", "AlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                        null, null);
            }

            // 工单级并发锁
            string lockKey = $"{req.WorkOrderNo}_{req.AssemblySeq}";
            var jobLock = await TryAcquireLockAsync(lockKey, traceId, operatorId);
            if (jobLock == null)
            {
                Logger.LogWarning("[{TraceId}] 入库失败：获取锁超时，工单 {WorkOrderNo}，操作员 {Operator}", traceId, req.WorkOrderNo, operatorId);
                return (false, GetText("Receipt", "SystemBusy"), null, null);
            }

            // receiptService 提前声明，供 catch 中读取执行阶段的 Epicor API 错误
            EpicorReceiptService? receiptService = null;
            try
            {
                // 双重幂等性检查：命中时同样按结果码区分应答
                var lockedResult = TryGetRequestResult(requestId);
                if (lockedResult != null)
                {
                    bool lockedSucceeded = lockedResult == IBusinessLockProvider.RequestResultSuccess;
                    Logger.LogInformation("[{TraceId}] 幂等请求已处理（锁内二次检查）: RequestId={RequestId}，结果={Result}", traceId, requestId, lockedResult);
                    return (lockedSucceeded,
                            lockedSucceeded ? GetText("Receipt", "AlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                            null, null);
                }

                // ===== 1. 工单验证 =====
                // receiptInfo 为验证阶段回填的仓库/库位/单位/物料信息，提交阶段用于补齐用户未显式填写的字段
                var (jobValid, jobMsg, receiptInfo, jobOverReceiptWarn) = await ValidateJobForReceiptAsync(
                       req.WorkOrderNo, req.AssemblySeq, req.Quantity, req.MaterialCode);
                if (!jobValid)
                {
                    Logger.LogWarning("[{TraceId}] 入库失败：工单验证未通过，工单 {WorkOrderNo}，原因 {Reason}", traceId, req.WorkOrderNo, jobMsg);
                    return (false, jobMsg, null, null);
                }

                // ===== 2. 执行入库（单阶段，6 步一次走完） =====
                receiptService = await BusinessServiceFactory.CreateReceiptServiceAsync(operatorId);

                string lotNum = string.IsNullOrEmpty(req.BatchNo) ? req.WorkOrderNo : req.BatchNo;
                // 用户显式填写优先，缺失时才用验证阶段回填值补齐，避免"校验阶段用的仓库/库位/物料 ≠ 提交阶段"的错位
                string whseCode = !string.IsNullOrWhiteSpace(req.WarehouseCode)
                    ? req.WarehouseCode
                    : (receiptInfo?.WarehouseCode ?? string.Empty);
                string binNum = !string.IsNullOrWhiteSpace(req.Location)
                    ? req.Location
                    : (receiptInfo?.BinNum ?? string.Empty);
                string partNum = !string.IsNullOrWhiteSpace(req.MaterialCode)
                    ? req.MaterialCode
                    : (receiptInfo?.PartNum ?? string.Empty);

                var (result, warningMessage) = await receiptService.ExecuteReceiptAsync(
                    jobNum: req.WorkOrderNo,
                    assemblySeq: req.AssemblySeq,
                    partNum: partNum,
                    whseCode: whseCode,
                    binNum: binNum,
                    lotNum: lotNum,
                    quantity: req.Quantity);

                if (result == null || receiptService.HasApiError)
                {
                    string errMsg = receiptService.LastApiError ?? GetText("Receipt", "ReceiptApiFailed");
                    Logger.LogError("[{TraceId}] 入库失败：工单 {WorkOrderNo}，物料 {MaterialCode}，错误: {Error}",
                        traceId, req.WorkOrderNo, req.MaterialCode, errMsg);
                    DocumentOperationLogger.Log("工单入库", $"工单{req.WorkOrderNo}/装配{req.AssemblySeq}/物料{req.MaterialCode}/数量{req.Quantity}", operatorId, success: false);
                    // 幂等：失败同样落标记，且标记为"结果未知"。若本次实际已在 Epicor 侧入库
                    // （仅响应解析/网络失败），客户端用同一 X-Request-ID 重放时将被拦截，
                    // 避免重复入库；同时命中应答不会把"未确认成功"回报成成功。
                    MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);
                    return (false, errMsg, null, null);
                }

                // 成功：写入幂等性缓存（Epicor 已入库，缓存失败不应掩盖成功结果）
                try
                {
                    MarkRequestProcessed(requestId);
                }
                catch (Exception markEx)
                {
                    Logger.LogWarning(markEx, "[{TraceId}] MarkRequestProcessed 失败（Epicor 入库已成功，仅幂等标记失败）：RequestId {RequestId}",
                        traceId, requestId);
                }

                // 单据操作明细：入库提交成功
                DocumentOperationLogger.Log("工单入库", $"工单{req.WorkOrderNo}/装配{req.AssemblySeq}/物料{req.MaterialCode}/数量{req.Quantity}", operatorId, success: true);

                // 合并超收提示（工单验证阶段产生）与 Epicor 入库返回的警告，一并透传给前端
                string finalWarning = string.Join("；",
                    new[] { jobOverReceiptWarn, warningMessage }.Where(s => !string.IsNullOrEmpty(s)));

                Logger.LogInformation(
                    "[{TraceId}] 入库成功：工单 {WorkOrderNo}，装配 {AssemblySeq}，物料 {MaterialCode}，数量 {Quantity}，操作员 {Operator}，警告={Warning}",
                    traceId, req.WorkOrderNo, req.AssemblySeq, req.MaterialCode, req.Quantity, operatorId,
                    string.IsNullOrEmpty(finalWarning) ? "无" : finalWarning);

                var receiptResult = new ReceiveMfgPartResult
                {
                    WorkOrderNo = req.WorkOrderNo,
                    AssemblySeq = req.AssemblySeq,
                    MaterialCode = req.MaterialCode,
                    Quantity = req.Quantity,
                    BatchNo = req.BatchNo,
                    Location = req.Location,
                    Operator = operatorId,
                    ReceiptTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    WarningMessage = finalWarning
                };

                return (true, GetText("Receipt", "SubmitSuccess"), receiptResult, finalWarning);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{TraceId}] 入库过程异常：工单 {WorkOrderNo}", traceId, req.WorkOrderNo);

                // 单据操作明细：入库提交异常按失败记录
                DocumentOperationLogger.Log("工单入库", $"工单{req.WorkOrderNo}/装配{req.AssemblySeq}/物料{req.MaterialCode}/数量{req.Quantity}", operatorId, success: false);

                // 幂等：异常同样落标记并标记为"结果未知"（异常可能发生在"Epicor 已入库、仅响应解析失败"之后），
                // 防止客户端用同一 X-Request-ID 重放造成重复入库，且不得回报成功。
                MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);

                // Epicor API 业务错误透传：执行阶段各步骤抛异常前已写入 receiptService.LastApiError
                if (receiptService != null && receiptService.HasApiError)
                    return (false, receiptService.LastApiError ?? GetText("Receipt", "ReceiptApiFailed"), null, null);

                // 兜底：若异常链中携带 Epicor 业务错误原文（"[数字] ..." 或含 CorrelationId），仍回传原文
                string? epicorMessage = ExtractEpicorBusinessError(ex);
                if (!string.IsNullOrEmpty(epicorMessage))
                    return (false, epicorMessage, null, null);

                // 未预期异常：Message 可能含主机名/URL 等内部信息，对外统一返回中性文案
                return (false, GetText("Receipt", "SystemProcessingError"), null, null);
            }
            finally
            {
                ReleaseLock(jobLock, lockKey, traceId);
            }
        }

        #endregion
    }
}
