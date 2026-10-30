using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WarehouseScanner.Services;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 发货业务服务
    /// 职责：封装发货流程全部核心业务逻辑（订单验证 → 库存验证 → 发货执行）
    /// 通用基础设施（锁 / 幂等 / TraceId / 依赖注入）由 <see cref="BaseBusinessService"/> 提供。
    /// 
    /// 架构原则：Controller 只做请求调度，业务逻辑下沉到 BusinessService
    /// </summary>
    public class OutboundBusinessService : BaseBusinessService
    {
        // 锁键前缀：与 Return/Transfer/Issue/Receipt 等业务服务在共享锁字典中相互隔离
        private const string LockPrefix = "Outbound";
        private const string CacheKeyPrefix = "OutboundRequest:";

        public OutboundBusinessService(
            ILogger<OutboundBusinessService> logger,
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

        #region 1. 订单验证

        /// <summary>
        /// 验证销售订单是否存在且可发货
        /// </summary>
        /// <param name="orderNum">订单号</param>
        /// <param name="orderLine">订单行</param>
        /// <param name="orderRelNum">订单关系号</param>
        /// <param name="materialCode">物料代码</param>
        /// <param name="unit">单位</param>
        /// <returns>(success, message, shipmentDetail, outboundQty) outboundQty=订单可发量</returns>
        public async Task<(bool success, string message, EpicorAuthService.OrderRelInfo? shipmentDetail, decimal outboundQty)> ValidateOrderAsync(
            string orderNum, int orderLine, int orderRelNum, string materialCode, string unit)
        {
            if (string.IsNullOrWhiteSpace(orderNum))
                return (false, GetText("Outbound", "OrderNumRequired"), null, 0);

            // 查订单行详情。错误标记只能读 authService，不能读 QueryGateway：
            // 网关是另一个对象，它的 HasApiError 同步不到这里的调用结果，
            // 读它会把这个 Count==0 分支的真实接口报错掩盖成"未找到订单"。
            var authService = BusinessServiceFactory.CreateAuthService();
            var shipmentList = await authService.QueryOrderRelsBySalesOrder(orderNum, orderLine);
            if (authService.HasApiError)
            {
                // 订单行不存在时 Epicor 返回 "[404] Entity OrderDtl with specified keys is not found"，
                // 这属于"订单行不存在"而非接口故障：按业务语义提示更贴合操作员认知
                // （原文已由 CheckEpicorApiError 记入服务端日志，排查时仍可查）。
                if (IsRecordNotFoundApiError(authService.LastApiError))
                    return (false, string.Format(GetText("Outbound", "OrderNotFound"), orderNum, orderLine), null, 0);

                return (false, string.Format(GetText("Outbound", "EpicorApiFailed"), authService.LastApiError), null, 0);
            }

            if (shipmentList.Count == 0)
                return (false, string.Format(GetText("Outbound", "OrderNotFound"), orderNum, orderLine), null, 0);

            var shipmentDetail = shipmentList?.FirstOrDefault(x => x.OrderRelNum == orderRelNum);
            if (shipmentDetail == null)
                return (false, string.Format(GetText("Outbound", "OrderRelNotFound"), orderNum, orderLine, orderRelNum), null, 0);

            // 检查订单确认状态（ShipLocked_c 表示"订单确认可以发货"，并非禁止发货）
            if (!IsTrueFlag(shipmentDetail.ShipLocked_c))
                return (false, GetText("Outbound", "OrderLocked"), null, 0);

            // 检查固定发布状态
            if (!IsTrueFlag(shipmentDetail.FirmRelease))
                return (false, GetText("Outbound", "FirmRelease"), null, 0);

            // 检查订单状态（与 ValidateOrder 接口口径一致，防止绕过前端直接提交已结订单）
            if (!IsOpenStatus(shipmentDetail.Status))
                return (false, GetText("Outbound", "OrderStatus"), null, 0);

            // 检查物料匹配
            if (shipmentDetail.PartNum != materialCode)
                return (false, string.Format(GetText("Outbound", "MaterialMismatch"), materialCode, shipmentDetail.PartNum), null, 0);

            // 检查单位匹配：单位为空时跳过比对（与 ValidateInventory 的"单位为空不参与校验"口径一致，
            // 避免控制器校验通过、提交时却硬失败并报出"单位不匹配：请求 ，系统 X"的困惑文案）
            if (!string.IsNullOrWhiteSpace(unit) &&
                !string.Equals(shipmentDetail.IUM, unit, StringComparison.OrdinalIgnoreCase))
                return (false, string.Format(GetText("Outbound", "UnitMismatch"), unit, shipmentDetail.IUM), null, 0);

            // 检查剩余发货量
            // 后端上限口径 = OrderQty − TotalJobStockShipped（OData 标准字段）；
            // 前端「未出货」门槛 = 计划出货 − 已发货（BAQ SO_SchdUpdate：OrderRel_SellingReqQty / Calculated_reltotalqty）。
            // 注意：二者"相等"目前只是推断（计划出货 ≈ OrderQty、已发货 ≈ TotalJobStockShipped），尚未实机核对。
            // 若核对后发现不等，前后端门槛将不同源（前端放行的数量会被下方 Math.Min 静默截断，
            // 且直接调接口绕过前端时后端不按"未出货"口径拦截），届时需改为与 BAQ 口径同源后再提交发货。
            decimal outboundQty = shipmentDetail.OrderQty - shipmentDetail.TotalJobStockShipped;
            if (outboundQty <= 0)
                return (false, string.Format(GetText("Outbound", "OrderFullyShippedDetail"), orderNum, materialCode), null, 0);

            return (true, GetText("Outbound", "OrderValidationPassed"), shipmentDetail, outboundQty);
        }

        #endregion

        #region 2. 库存验证

        /// <summary>
        /// 校验所选仓库/库位/批次下是否存在库存，并返回库存数
        /// 库存数 = Σ 在手数量 QtyOnHand（BAQ Inve_view 不返回已分配数量，故不做扣减；本方法不做数量拦截）
        /// </summary>
        /// <param name="partNum">物料代码</param>
        /// <param name="warehouse">仓库代码</param>
        /// <param name="binNum">库位</param>
        /// <param name="lotNum">批次号</param>
        /// <returns>(success, message, stockQty) stockQty = Σ 在手数量</returns>
        public async Task<(bool success, string message, decimal stockQty)> ValidateInventoryAsync(
            string partNum, string warehouse, string binNum, string lotNum)
        {
            if (string.IsNullOrWhiteSpace(partNum))
                return (false, GetText("Outbound", "PartNumRequired"), 0);

            // 库存查询：与页面/校验接口同源，调 BAQ Inve_view（入参 物料+仓库+库位+批次），
            // 不走 PartBinSearch BO——其过滤维度取值不一致时会返回 0 行，出现"有库存却提示无库存"。
            var inventoryList = await QueryGateway.GetInventoryDetailByInveViewBAQAsync(partNum, warehouse, binNum, lotNum);
            if (QueryGateway.HasApiError)
                return (false, string.Format(GetText("Outbound", "EpicorApiFailed"), QueryGateway.LastApiError), 0);

            if (inventoryList == null || !inventoryList.Any())
                return (false, GetText("Outbound", "NoAvailableInventory"), 0);

            // 库存数 = Σ 在手数量（BAQ Inve_view 不返回已分配数量，故不做扣减）
            decimal stockQty = inventoryList.Sum(x => x.QtyOnHand);
            if (stockQty <= 0)
                return (false, GetText("Outbound", "NoAvailableInventory"), 0);

            return (true, GetText("Outbound", "InventoryValidationPassed"), stockQty);
        }

        #endregion

        #region 3. 执行发货

        /// <summary>
        /// 执行发货操作
        /// 所有验证阶段只调用一次 API，结果向下传递
        /// </summary>
        /// <param name="model">发货请求模型</param>
        /// <param name="operatorId">操作员ID</param>
        /// <param name="requestId">请求ID（用于幂等性）</param>
        /// <returns>(success, message, data)</returns>
        public async Task<(bool success, string message, object? data)> ShipOrderAsync(
            ShipmentRequest model, string operatorId, string? requestId = null)
        {
            string traceId = GetTraceId();

            // 参数校验：Controller 层已做必填校验（本地化文案），此处仅做防御性 null 检查
            if (model == null)
            {
                Logger.LogWarning("[{TraceId}] 发货失败：请求参数为空，操作员 {Operator}", traceId, operatorId);
                return (false, GetText("Outbound", "RequestParamEmpty"), null);
            }

            Logger.LogInformation("[{TraceId}] 发货开始：订单 {OrderNum}，行 {OrderLine}，物料 {MaterialCode}，数量 {Quantity}，操作员 {Operator}",
                traceId, model.SalesOrderNo, model.OrderLine ?? 1, model.MaterialCode, model.Quantity, operatorId);

            // 幂等性前置检查：命中时必须区分"上次成功"与"结果未知"，否则会把失败误报成成功
            var previousResult = TryGetRequestResult(requestId);
            if (previousResult != null)
            {
                bool previousSucceeded = previousResult == IBusinessLockProvider.RequestResultSuccess;
                Logger.LogInformation("[{TraceId}] 幂等请求已处理: RequestId={RequestId}，结果={Result}", traceId, requestId, previousResult);
                return (previousSucceeded,
                        previousSucceeded ? GetText("Outbound", "RequestAlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                        null);
            }

            // 订单级并发锁（基于订单+行+关系号）
            string lockKey = $"{model.SalesOrderNo}_{model.OrderLine ?? 1}_{model.OrderRelNum ?? 1}";
            var jobLock = await TryAcquireLockAsync(lockKey, traceId, operatorId);
            if (jobLock == null)
            {
                Logger.LogWarning("[{TraceId}] 发货失败：获取锁超时，订单 {OrderNum}，操作员 {Operator}", traceId, model.SalesOrderNo, operatorId);
                return (false, GetText("Outbound", "SystemBusy"), null);
            }

            // execAuthService 提前声明，供 catch 中读取执行阶段的 Epicor API 具体错误（如"无效仓库和库位"）。
            // 注意：ValidateOrderAsync 内部会创建另一个独立的 authService 实例，其错误通过返回值处理，不会到 catch。
            EpicorAuthService? execAuthService = null;
            try
            {
                // 双重幂等性检查：命中同样必须区分成功与结果未知
                var lockedResult = TryGetRequestResult(requestId);
                if (lockedResult != null)
                {
                    bool lockedSucceeded = lockedResult == IBusinessLockProvider.RequestResultSuccess;
                    Logger.LogInformation("[{TraceId}] 幂等请求已处理（锁内二次检查）: RequestId={RequestId}，结果={Result}", traceId, requestId, lockedResult);
                    return (lockedSucceeded,
                            lockedSucceeded ? GetText("Outbound", "RequestAlreadyProcessed") : GetText("Common", "IdempotentResultUnknown"),
                            null);
                }

                // ===== 验证阶段：每个 API 只调用一次 =====

                // 1. 订单验证（1 次 API）：同时返回订单可发量 outboundQty，避免重复计算
                var (orderValid, orderMessage, shipmentDetail, outboundQty) = await ValidateOrderAsync(
                    model.SalesOrderNo,
                    model.OrderLine ?? 1,
                    model.OrderRelNum ?? 1,
                    model.MaterialCode,
                    model.Unit
                );

                if (!orderValid)
                {
                    Logger.LogWarning("[{TraceId}] 发货失败：订单验证未通过，订单 {OrderNum}，行 {OrderLine}，原因 {Reason}", traceId, model.SalesOrderNo, model.OrderLine, orderMessage);
                    return (false, orderMessage, null);
                }

                // 2. 库存验证（1 次 API）：库存数 = Σ 在手数量（BAQ Inve_view 不返回已分配数量）
                var (inventoryValid, inventoryMessage, stockQty) = await ValidateInventoryAsync(
                    model.MaterialCode, model.WarehouseCode, model.Location, model.BatchNo);

                if (!inventoryValid)
                {
                    Logger.LogWarning("[{TraceId}] 发货失败：库存验证未通过，物料 {MaterialCode}，仓库 {Warehouse}，库位 {Location}，原因 {Reason}",
                        traceId, model.MaterialCode, model.WarehouseCode, model.Location, inventoryMessage);
                    return (false, inventoryMessage, null);
                }

                // 3. 计算实际发货量：订单可发量、库存数、请求数量三者取最小
                decimal actualShipQty = Math.Min(Math.Min(outboundQty, stockQty), model.Quantity);

                if (actualShipQty <= 0)
                {
                    Logger.LogWarning("[{TraceId}] 发货失败：发货数量无效，请求 {ReqQty}，库存数 {StockQty}，订单可发 {OutboundQty}，订单 {OrderNum}，物料 {MaterialCode}",
                        traceId, model.Quantity, stockQty, outboundQty, model.SalesOrderNo, model.MaterialCode);
                    return (false, GetText("Outbound", "InvalidQuantity"), null);
                }

                // 发货数量被截断时（超过库存数 或 超过订单可发量），通知前端弹出提示框
                bool quantityReduced = model.Quantity > actualShipQty;
                // 确定被哪个限制截断，用于提示文案
                string limitReason = stockQty < outboundQty
                    ? string.Format(GetText("Outbound", "LimitReasonStock"), stockQty)
                    : string.Format(GetText("Outbound", "LimitReasonOrder"), outboundQty);

                // ===== 执行阶段 =====

                execAuthService = BusinessServiceFactory.CreateAuthService();
                var outboundService = new EpicorOutboundService(execAuthService, execAuthService.Company);

                var result = await outboundService.CreateShipmentAsync(
                    orderNum: model.SalesOrderNo,
                    partNum: model.MaterialCode,
                    quantity: actualShipQty,
                    whseCode: model.WarehouseCode,
                    ShipViaCode: shipmentDetail!.ShipViaCode,
                    binNum: model.Location,
                    lotNum: model.BatchNo,
                    orderLine: model.OrderLine ?? 1,
                    orderRelNum: model.OrderRelNum ?? 1,
                    revisionNum: model.RevisionNum ?? "",
                    packNum: model.packId ?? 1
                );

                // 统一检查 Epicor API 错误（与 IssueBusinessService/ReturnBusinessService/TransferBusinessService 保持一致）
                if (execAuthService.HasApiError)
                {
                    string errMsg = execAuthService.LastApiError ?? GetText("Outbound", "ShipmentApiFailed");
                    Logger.LogError("[{TraceId}] 发货失败（Epicor API 错误）：订单 {OrderNum}，行 {OrderLine}，物料 {MaterialCode}，数量 {Quantity}，错误: {Error}",
                        traceId, model.SalesOrderNo, model.OrderLine, model.MaterialCode, actualShipQty, errMsg);
                    DocumentOperationLogger.Log("销售出货",
                        $"订单{model.SalesOrderNo}/行{model.OrderLine ?? 1}/物料{model.MaterialCode}/数量{actualShipQty}",
                        operatorId, success: false);
                    // 幂等区分两类失败：
                    // - 确定性失败：响应体中能提取到 BO 错误信息（顶层 ErrorMessage，或嵌套 returnObj.ErrorMessage，
                    //   与 CheckEpicorApiError 判定口径一致），说明 Epicor 明确拒绝、事务未提交，
                    //   不落"未知"标记，操作员修正数据后用同一 X-Request-ID 重试应允许重执行；
                    // - 真未知失败：仅有 HasApiError 但取不到 BO 错误体（传输/超时类），本次可能已在
                    //   Epicor 侧建单，仍落"未知"标记拦截重放，防止重复发货。
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

                // 成功：写入幂等性缓存
                MarkRequestProcessed(requestId);

                // 单据操作明细：出货提交成功
                DocumentOperationLogger.Log("销售出货",
                    $"订单{model.SalesOrderNo}/行{model.OrderLine ?? 1}/物料{model.MaterialCode}/数量{actualShipQty}",
                    operatorId, success: true);

                Logger.LogInformation("[{TraceId}] 发货成功：订单 {OrderNum}，行 {OrderLine}，物料 {PartNum}，数量 {Quantity}，操作员 {Operator}",
                    traceId, model.SalesOrderNo, model.OrderLine, model.MaterialCode, actualShipQty, operatorId);

                if (quantityReduced)
                {
                    Logger.LogInformation("[{TraceId}] 发货数量已自动调整：请求 {ReqQty}，{LimitReason}，实际 {ActualQty}",
                        traceId, model.Quantity, limitReason, actualShipQty);
                    return (true,
                        string.Format(GetText("Outbound", "ShipQtyAdjusted"), model.Quantity, limitReason, actualShipQty),
                        new { quantityReduced = true, requestedQty = model.Quantity, actualShipQty = actualShipQty });
                }

                // 普通成功：data 统一携带 actualShipQty，供前端提示与对账使用
                return (true, GetText("Outbound", "SubmitSuccess"), new { actualShipQty = actualShipQty, epicorResult = result });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{TraceId}] 发货过程异常：订单 {OrderNum}，行 {OrderLine}", traceId, model.SalesOrderNo, model.OrderLine);

                // 幂等：异常同样落标记（异常可能发生在"Epicor 已建单、仅响应解析失败"之后），
                // 防止客户端用同一 X-Request-ID 重放造成重复发货；按"未知"落库。
                MarkRequestProcessed(requestId, IBusinessLockProvider.RequestResultUnknown);

                // 单据操作明细：出货提交异常按失败记录
                DocumentOperationLogger.Log("销售出货",
                    $"订单{model.SalesOrderNo}/行{model.OrderLine ?? 1}/物料{model.MaterialCode}/数量{model.Quantity}",
                    operatorId, success: false);

                // Epicor API 业务错误（如"无效仓库和库位"）：CreateShipmentAsync 内各步骤抛异常前，
                // 底层 PostBoJsonAsync 已通过 CheckEpicorApiError 写入 execAuthService.LastApiError，
                // 此处应把具体失败原因原样返回给前端，让操作员能看到真实错误，而非笼统文案。
                if (execAuthService != null && execAuthService.HasApiError)
                {
                    return (false, execAuthService.LastApiError ?? GetText("Outbound", "ShipmentApiFailed"), null);
                }

                // EpicorOutboundService 抛出的 InvalidOperationException 均为受控业务文案
                // （如"装箱单号 X 不存在"、"保存发货明细失败"，或 EpicorJsonHelper.ParseResponse 解析出的 Epicor 原文），
                // 不含主机名/URL 等内部信息，可直接展示给操作员。
                if (ex is InvalidOperationException)
                {
                    return (false, ex.Message, null);
                }

                // 兜底：对非 InvalidOperationException 的异常（如网络异常被包装等），
                // 若异常链中携带 Epicor 业务错误原文（"[数字] ..." 或含 CorrelationId），仍回传原文。
                string? epicorMessage = ExtractEpicorBusinessError(ex);
                if (!string.IsNullOrEmpty(epicorMessage))
                {
                    return (false, epicorMessage, null);
                }

                // 未预期异常（非 Epicor 业务错误）：Message 可能含主机名/URL 等内部信息，
                // 因此只写服务端日志，对外统一返回中性文案。
                return (false, GetText("Outbound", "ShipFailedRetry"), null);
            }
            finally
            {
                ReleaseLock(jobLock, lockKey, traceId);
            }
        }

        /// <summary>
        /// 判断 Epicor 错误是否为"记录不存在"（订单行不存在）。
        /// 典型原文："[404] Entity OrderDtl with specified keys is not found (CorrelationId: xxx)"。
        /// 这类错误不是接口故障，前端按"未找到订单 X 的行 Y"提示更贴合操作员认知；
        /// 其余错误（许可证、无效仓库库位、服务异常等）一律回传原文，避免真实原因被掩盖。
        /// </summary>
        /// <param name="apiError">EpicorAuthService.LastApiError 的原文</param>
        /// <returns>true=属于"记录不存在"错误</returns>
        private static bool IsRecordNotFoundApiError(string? apiError)
        {
            if (string.IsNullOrWhiteSpace(apiError)) return false;

            return apiError.Contains("[404]", StringComparison.Ordinal)
                && apiError.Contains("not found", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断 Epicor 返回的状态字段是否为真：兼容 True/true/TRUE/1（避免大小写或"1"导致误判）。
        /// 声明为 internal 供 OutboundController 复用，避免同一布尔字段在控制器与业务服务出现两套判定。
        /// </summary>
        internal static bool IsTrueFlag(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var v = value.Trim();
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }

        /// <summary>
        /// 判断订单状态是否允许发货：兼容 Epicor 英文 Open / 中文 未结
        /// （与 ValidateOrder 接口口径保持一致，避免绕过前端直接提交已结订单）。
        /// </summary>
        private static bool IsOpenStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return false;

            var v = status.Trim();
            return v.Equals("Open", StringComparison.OrdinalIgnoreCase) || v == "未结";
        }

        #endregion
    }
}