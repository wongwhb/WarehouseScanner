using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;
namespace WarehouseScannerCore.Controllers
{
    /// <summary>AJAX - 验证采购订单 / 提交采购收货</summary>
    public partial class ReceivinController
    {
        #region AJAX - 验证采购订单
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidatePO([FromBody] ValidatePORequest request)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage);
                return Json(new { success = false, message = string.Format(GetText("Receivin", "InvalidParameters"), string.Join(" | ", errors)) });
            }

            // F-3（后端兜底）：POLine<=0 时 BAQ 未按行号过滤，会命中该 PO 的任意行并返回错误行数据。
            // 前端已在手动扫码与组合扫码两条路径强制行号，此处拦截绕过前端直接提交的调用。
            if (request.POLine <= 0)
            {
                _logger.LogWarning("ValidatePO 无效行号: PONum={PONum}, POLine={POLine}", request.PONum, request.POLine);
                return Json(new { success = false, message = GetText("Receivin", "POLineNoInvalid") });
            }

            try
            {
                var authService = CreateEpicorAuthServiceFromSession();
                // 补齐操作员 ID 透传给 EpicorReceiptService，满足审计链路要求
                string opId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";

                // Step 1: 通过 OpenPODetail BAQ 查询PO明细信息
                var baqResult = await QueryPOInfoByBAQAsync(
                    authService,
                    request.PONum,
                    request.POLine > 0 ? request.POLine : (int?)null,
                    !string.IsNullOrEmpty(request.PartNum) ? request.PartNum : null);

                if (baqResult == null)
                {
                    // poNotFound 标记给前端：整单无效，前端收到后清空所有录入框，便于重扫新 PO
                    return Json(new { success = false, poNotFound = true, message = string.Format(GetText("Receivin", "PONotFound"), request.PONum) });
                }

                // 验证订单数量大于0
                if (baqResult.RelQty <= 0)
                {
                    return Json(new { success = false, message = string.Format(GetText("Receivin", "POQtyZero"), request.PONum) });
                }

                // ============ PO / 行状态显式校验（仓库侧前置拦截，Epicor BO 再做最终兜底）============
                // 校验字段：
                //   PODetail.OpenLine = false        → 行已结，无法收货，清空录入框让操作员重扫
                //   POHeader.ApprovalStatus ≠ "A"   → PO 未审核（U=未审核），无法收货，不清空录入框
                // 字段缺列时放行交给 Epicor ValidatePONum 兜底，避免 BAQ 配置差异误拦。

                // 1. 行已结（OpenLine 字段明确返回时）
                if (baqResult.OpenLine == false)
                {
                    _logger.LogWarning("ValidatePO 拦截已结行: PONum={PONum}, POLine={POLine}, OpenLine=false",
                        request.PONum, request.POLine);
                    return Json(new { success = false, poNotFound = true, message = string.Format(GetText("Receivin", "POLineClosed"), request.PONum, request.POLine) });
                }

                // 2. PO 未审核（ApprovalStatus 字段明确返回且不为 A）
                if (!string.IsNullOrEmpty(baqResult.ApprovalStatus) &&
                    !string.Equals(baqResult.ApprovalStatus, "A", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("ValidatePO 拦截未审核 PO: PONum={PONum}, POLine={POLine}, ApprovalStatus={ApprovalStatus}",
                        request.PONum, request.POLine, baqResult.ApprovalStatus);
                    return Json(new { success = false, message = string.Format(GetText("Receivin", "PONotApproved"), request.PONum) });
                }

                // 单位一致性校验：扫码单位必须与采购订单单位（IUM）一致。
                // 渐进式扫码（先扫 PO 后扫物料）时扫码单位可能尚未录入，此时跳过，
                // 由 CompleteReceipt 提交侧必填并兜底校验，避免过早拦截正常扫码流程。
                if (!string.IsNullOrWhiteSpace(request.Unit) &&
                    !string.IsNullOrWhiteSpace(baqResult.IUM) &&
                    !string.Equals(request.Unit, baqResult.IUM, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("单位不一致（ValidatePO）: PONum={PONum}, POLine={POLine}, ScanUnit={ScanUnit}, POUnit={POUnit}",
                        request.PONum, request.POLine, request.Unit, baqResult.IUM);
                    return Json(new
                    {
                        success = false,
                        unitMismatch = true,
                        message = string.Format(GetText("Receivin", "UnitMismatch"), request.Unit, baqResult.IUM)
                    });
                }

                // Epicor 允许超收：扫码数量 + 已收数量超过订单数量时只告警不拦截。
                // 超收警告透传给前端弹提示，校验照常通过，用户确认后可直接收货。
                string? overReceiptWarning = null;
                if (request.Quantity > 0)
                {
                    decimal totalQty = request.Quantity + baqResult.ReceivedQty;
                    if (totalQty > baqResult.RelQty)
                    {
                        _logger.LogWarning("超收提示: PONum={PONum}, POLine={POLine}, ScanQty={ScanQty}, ReceivedQty={ReceivedQty}, RelQty={RelQty}",
                            request.PONum, request.POLine, request.Quantity, baqResult.ReceivedQty, baqResult.RelQty);
                        overReceiptWarning = string.Format(GetText("Receivin", "OverReceiptWarning"), request.Quantity, baqResult.ReceivedQty, totalQty, baqResult.RelQty, baqResult.AvailableQty);
                    }
                }

                // ============ 处理仓库和库位 ============
                string warehouseCode = request.WarehouseCode ?? string.Empty;
                string binNum = request.Location ?? string.Empty;

                bool warehouseProvided = !string.IsNullOrEmpty(request.WarehouseCode);
                // EpicorReceiptService 单实例复用，避免同一请求内重复构造
                var receiptService = new EpicorReceiptService(authService, authService.Company ?? "", operatorId: opId, plant: authService.Plant);

                if (!warehouseProvided)
                {
                    // 前端未填写仓库 → 自动获取默认仓库和库位
                    string partNumForLookup = !string.IsNullOrEmpty(request.PartNum) ? request.PartNum : baqResult.PartNum ?? "";
                    if (!string.IsNullOrEmpty(partNumForLookup))
                    {
                        try
                        {
                            var defaultInfo = await receiptService.GetPartDefaultWarehouseAsync(partNumForLookup);
                            if (defaultInfo.HasValue && !defaultInfo.Value.partNotFound && !string.IsNullOrEmpty(defaultInfo.Value.warehouseCode))
                            {
                                warehouseCode = defaultInfo.Value.warehouseCode;

                                string defaultBinNum = await receiptService.GetWhseBinDefaultAsync(warehouseCode);
                                if (!string.IsNullOrEmpty(defaultBinNum))
                                {
                                    binNum = defaultBinNum;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "获取默认仓库/库位失败，不影响主流程");
                        }
                    }
                }
                // 前端已填仓库时无需特殊处理

                // 验证仓库存在性
                // 网络异常不能假性通过：ValidatePO 的职责就是预校验，异常直接返回失败，
                //      避免问题拖到 CompleteReceipt 才暴露。
                if (!string.IsNullOrEmpty(warehouseCode))
                {
                    try
                    {
                        var (whseExists, _) = await receiptService.CheckWarehouseExistsAsync(warehouseCode);
                        if (!whseExists)
                        {
                            return Json(new { success = false, message = string.Format(GetText("Receivin", "WarehouseNotExistCannotReceive"), warehouseCode) });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "仓库验证异常: WarehouseCode={WarehouseCode}", warehouseCode);
                        return Json(new { success = false, message = string.Format(GetText("Receivin", "WarehouseValidationFailed"), warehouseCode) });
                    }
                }

                // 验证库位存在性
                if (!string.IsNullOrEmpty(binNum))
                {
                    try
                    {
                        var (binExists, _) = await receiptService.CheckBinExistsAsync(warehouseCode, binNum);
                        if (!binExists)
                        {
                            return Json(new { success = false, message = string.Format(GetText("Receivin", "BinNotExistCannotReceive"), binNum) });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "库位验证异常: WarehouseCode={WarehouseCode}, BinNum={BinNum}", warehouseCode, binNum);
                        return Json(new { success = false, message = string.Format(GetText("Receivin", "BinValidationFailed"), binNum) });
                    }
                }

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        poNum = baqResult.PoNo,
                        // 返回用户输入的 request.POLine 为准：
                        //      BAQ 可能命中其他行，baqResult.PoLine 不一定与请求一致，避免前后端行号错位。
                        poLine = request.POLine,
                        partNum = baqResult.PartNum,
                        relQty = baqResult.RelQty,
                        receivedQty = baqResult.ReceivedQty,
                        availableQty = baqResult.AvailableQty,
                        overReceipt = overReceiptWarning != null,
                        overReceiptWarning,
                        poStatus = "Open",
                        warehouseCode,
                        binNum
                    }
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("Receivin", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证采购订单异常: PONum={PONum}", request?.PONum);
                return Json(new { success = false, message = GetText("Receivin", "ValidatePOError") });
            }
        }
        #endregion

        #region AJAX - 提交采购收货
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteReceipt(
            [FromBody] CompleteReceiptRequest request,
            [FromHeader(Name = "X-Request-ID")] string? requestId = null)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage);
                return Json(new { success = false, message = string.Format(GetText("Receivin", "InvalidParameters"), string.Join(" | ", errors)) });
            }

            // ============ 业务参数显式校验（防御非浏览器入口与前端漏校验）============
            // 与 OutboundController 校验 OrderLine/OrderRelNum 正整数同源约束
            if (string.IsNullOrWhiteSpace(request.PONum))
                return Json(new { success = false, message = GetText("Receivin", "PONoRequired") });
            if (request.POLine <= 0)
                return Json(new { success = false, message = GetText("Receivin", "POLineMustBePositive") });
            if (request.Quantity <= 0)
                return Json(new { success = false, message = GetText("Receivin", "QtyMustBePositive") });
            if (string.IsNullOrWhiteSpace(request.PackSlip))
                return Json(new { success = false, message = GetText("Receivin", "DeliveryNoteRequired") });
            if (string.IsNullOrWhiteSpace(request.PartNum))
                return Json(new { success = false, message = GetText("Receivin", "MaterialCodeRequired") });
            if (string.IsNullOrWhiteSpace(request.Unit))
                return Json(new { success = false, message = GetText("Receivin", "UnitRequired") });

            string opId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";
            string trId = Guid.NewGuid().ToString("N");

            // ============ 幂等标识（X-Request-ID 请求头优先，其次请求体 RequestId）============
            string effectiveRequestId = !string.IsNullOrWhiteSpace(requestId)
                ? requestId!
                : (request.RequestId ?? string.Empty);

            if (string.IsNullOrWhiteSpace(effectiveRequestId))
            {
                // 未提供标识时用业务键兜底，确保"同一 PO + 行 + 送货单"的重复提交仍可被拦截
                // 兜底键必须包含数量与操作员：否则同一 PO 行 + 同一送货单的两次合法部分收货
                // （数量不同、由不同操作员提交）会被误判为重复提交并返回假成功（实际未过账）。
                effectiveRequestId = $"auto_{request.PONum}_{request.POLine}_{request.PackSlip}_{request.Quantity.ToString(CultureInfo.InvariantCulture)}_{opId}";
            }

            // 幂等缓存键加租户前缀：跨站点同 PO 号 + 行号 + PackSlip 不得误判"已处理过"
            string idempotentCacheKey = $"{CurrentTenantKey}|{CacheKeyPrefix}";

            // 幂等前置检查：已处理过则直接返回（不触碰 Epicor），避免重复收货
            if (_lockProvider.IsRequestProcessed(idempotentCacheKey, effectiveRequestId))
            {
                _logger.LogInformation("[{TraceId}] 采购收货重复提交（幂等命中）：用户 {Operator}，PO {PONum}，行 {POLine}，RequestId {RequestId}",
                    trId, opId, request.PONum, request.POLine, effectiveRequestId);
                return Json(new { success = true, message = GetText("Receivin", "ReceiptSuccessIdempotent"), isIdempotent = true });
            }

            // ============ 业务级并发锁（同一 PO + 行 + 送货单 串行化）============
            // 原 lockKey 仅 PO+POLine，不同 PackSlip 的收货会互相阻塞；
            //      且 POLine=0 时所有"未指定行号"请求共享同一锁。加入 PackSlip 细化锁粒度。
            // 加租户前缀：不同站点存在相同 PO 号时不得互相阻塞（与 Adjustment/CountTag 一致）。
            string lockKey = $"{CurrentTenantKey}|{request.PONum}_{request.POLine}_{request.PackSlip}";
            SemaphoreSlim? receiptLock = await _lockProvider.TryAcquireLockAsync(LockPrefix, lockKey, trId, opId);
            if (receiptLock == null)
            {
                _logger.LogWarning("[{TraceId}] 采购收货未取得并发锁（可能仍在处理中）：用户 {Operator}，PO {PONum}，行 {POLine}",
                    trId, opId, request.PONum, request.POLine);
                return Json(new { success = false, message = GetText("Receivin", "ReceiptInProgress") });
            }

            try
            {
                // 锁内二次幂等检查：并发请求被锁串行化后，首个请求可能已完成并标记幂等；
                // 后续请求在锁内才拿到执行权，此时若幂等缓存已命中则直接返回，避免重复过账。
                // 参考 IssueBusinessService 的双检模式。
                if (_lockProvider.IsRequestProcessed(idempotentCacheKey, effectiveRequestId))
                {
                    _logger.LogInformation("[{TraceId}] 采购收货重复提交（锁内幂等命中）：用户 {Operator}，PO {PONum}，行 {POLine}，RequestId {RequestId}",
                        trId, opId, request.PONum, request.POLine, effectiveRequestId);
                    return Json(new { success = true, message = GetText("Receivin", "ReceiptSuccessIdempotent"), isIdempotent = true });
                }

                var authService = CreateEpicorAuthServiceFromSession();

                _logger.LogInformation("[{TraceId}] 采购收货开始：用户 {Operator}，PO {PONum}，行 {POLine}，物料 {PartNum}，数量 {Quantity}，仓库 {Whse}/{Bin}，批次 {Batch}，PackSlip {PackSlip}",
                    trId, opId, request.PONum, request.POLine, request.PartNum, request.Quantity,
                    request.WarehouseCode, request.Location, request.BatchNo, request.PackSlip);

                // 分步失败辅助（多步流程必须记录当前步骤号）：
                // 统一输出 "失败于 Step N + 关键业务参数 + Epicor 错误"，失败时可直接定位 15 步流程的中断点。
                IActionResult Fail(int step, string message, bool poNotFound = false)
                {
                    _logger.LogError("[{TraceId}] 采购收货失败于 Step {Step}：用户 {Operator}，PO {PONum}，行 {POLine}，PackSlip {PackSlip}，原因 {Reason}，Epicor错误 {ApiError}",
                        trId, step, opId, request.PONum, request.POLine, request.PackSlip, message, authService.LastApiError);
                    // 单据操作明细：收货提交失败（任一步骤失败均未落地）
                    _documentOperationLogger.Log("采购收货",
                        $"PO{request.PONum}/行{request.POLine}/物料{request.PartNum}/数量{request.Quantity}/送货单{request.PackSlip}",
                        opId, success: false);
                    // poNotFound=true 时透传标记给前端：整单无效，前端清空所有录入框
                    if (poNotFound)
                        return Json(new { success = false, message, poNotFound = true });
                    return Json(new { success = false, message });
                }

                // ============ 验证仓库、库位、批次号不能为空 ============
                if (string.IsNullOrWhiteSpace(request.WarehouseCode))
                {
                    _logger.LogWarning("仓库验证失败: WarehouseCode 为空");
                    return Json(new { success = false, message = GetText("Receivin", "WarehouseCodeRequired") });
                }
                if (string.IsNullOrWhiteSpace(request.Location))
                {
                    _logger.LogWarning("库位验证失败: Location 为空");
                    return Json(new { success = false, message = GetText("Receivin", "LocationRequired") });
                }
                if (string.IsNullOrWhiteSpace(request.BatchNo))
                {
                    _logger.LogWarning("批次号验证失败: BatchNo 为空");
                    return Json(new { success = false, message = GetText("Receivin", "BatchNoRequired") });
                }

                // ============ 验证仓库和库位的有效性 ============
                var receiptService = new EpicorReceiptService(authService, authService.Company ?? "", operatorId: opId, plant: authService.Plant);

                // 验证仓库存在性
                var (whseExists, _) = await receiptService.CheckWarehouseExistsAsync(request.WarehouseCode);
                if (!whseExists)
                {
                    _logger.LogWarning("仓库验证失败: WarehouseCode={WarehouseCode} 不存在", request.WarehouseCode);
                    return Json(new { success = false, message = string.Format(GetText("Receivin", "WarehouseNotExist"), request.WarehouseCode) });
                }

                // 验证库位存在性（在指定仓库下）
                var (binExists, _) = await receiptService.CheckBinExistsAsync(request.WarehouseCode, request.Location);
                if (!binExists)
                {
                    _logger.LogWarning("库位验证失败: WarehouseCode={WarehouseCode}, Location={Location} 不存在", request.WarehouseCode, request.Location);
                    return Json(new { success = false, message = string.Format(GetText("Receivin", "LocationNotInWarehouse"), request.Location, request.WarehouseCode) });
                }


                // ============ 通过 BAQ 查询获取 PO 明细信息，同时校验 ============
                var baqResult = await QueryPOInfoByBAQAsync(
                    authService,
                    request.PONum,
                    request.POLine > 0 ? request.POLine : (int?)null,
                    !string.IsNullOrEmpty(request.PartNum) ? request.PartNum : null);

                // Step 0: 校验PO行信息
                if (baqResult == null)
                {
                    return Fail(0, string.Format(GetText("Receivin", "POLineNotFound"), request.PONum, request.POLine), poNotFound: true);
                }
                if (baqResult.RelQty <= 0)
                {
                    return Fail(0, string.Format(GetText("Receivin", "POLineQtyZero"), request.PONum, request.POLine));
                }
                // ============ PO / 行状态显式校验（提交侧兜底，防止 ValidatePO 后 PO 状态被其他系统变更）============
                // PODetail.OpenLine = false  → 行已结
                if (baqResult.OpenLine == false)
                {
                    _logger.LogWarning("[{TraceId}] CompleteReceipt 拦截已结行: 用户 {Operator}，PO {PONum}，行 {POLine}",
                        trId, opId, request.PONum, request.POLine);
                    return Fail(0, string.Format(GetText("Receivin", "POLineClosed"), request.PONum, request.POLine), poNotFound: true);
                }
                // POHeader.ApprovalStatus ≠ "A" → PO 未审核
                if (!string.IsNullOrEmpty(baqResult.ApprovalStatus) &&
                    !string.Equals(baqResult.ApprovalStatus, "A", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[{TraceId}] CompleteReceipt 拦截未审核 PO: 用户 {Operator}，PO {PONum}，行 {POLine}，ApprovalStatus={ApprovalStatus}",
                        trId, opId, request.PONum, request.POLine, baqResult.ApprovalStatus);
                    return Fail(0, string.Format(GetText("Receivin", "PONotApproved"), request.PONum));
                }
                // PORelNum 必须有效，否则 SetPORelNumInDs 会静默跳过，
                // 后续 Epicor 可能落到错误的 Release 上收货。
                if (baqResult.PORelNum <= 0)
                {
                    return Fail(0, string.Format(GetText("Receivin", "PORelNumUndetermined"), request.PONum, request.POLine));
                }
                // 校验扫码 PartNum 与 PO 行实际物料一致，防止实物料号与账套料号错位。
                // BAQ 查询以 request.PartNum 作为过滤条件，若返回的 PartNum 与请求不一致
                // （例如用户扫错料号但 BAQ 命中了别的行），必须在此拦截。
                if (!string.IsNullOrEmpty(baqResult.PartNum) &&
                    !string.Equals(request.PartNum, baqResult.PartNum, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("扫码 PartNum 与 PO 行 PartNum 不一致: PONum={PONum}, POLine={POLine}, ScanPartNum={ScanPartNum}, POPartNum={POPartNum}",
                        request.PONum, request.POLine, request.PartNum, baqResult.PartNum);
                    return Fail(0, string.Format(GetText("Receivin", "PartNumMismatch"), request.PartNum, request.PONum, request.POLine, baqResult.PartNum));
                }
                // 单位一致性校验（提交侧兜底）：ValidatePO 在扫码单位为空时放行，
                // 此处单位已必填，做最终拦截，防止绕过前端直接提交造成单位错位的收货。
                if (!string.IsNullOrEmpty(baqResult.IUM) &&
                    !string.Equals(request.Unit, baqResult.IUM, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("收货单位不一致: PONum={PONum}, POLine={POLine}, ScanUnit={ScanUnit}, POUnit={POUnit}",
                        request.PONum, request.POLine, request.Unit, baqResult.IUM);
                    return Fail(0, string.Format(GetText("Receivin", "UnitMismatch"), request.Unit, baqResult.IUM));
                }
                if (request.Quantity > 0)
                {
                    decimal totalQty = request.Quantity + baqResult.ReceivedQty;
                    if (totalQty > baqResult.RelQty)
                    {
                        // Epicor 允许超收：提交侧同样只记日志不拦截，收货流程继续。
                        // 操作员已在 ValidatePO 阶段看到超收警告弹窗并确认。
                        _logger.LogWarning("超收提交: PONum={PONum}, POLine={POLine}, ScanQty={ScanQty}, ReceivedQty={ReceivedQty}, RelQty={RelQty}",
                            request.PONum, request.POLine, request.Quantity, baqResult.ReceivedQty, baqResult.RelQty);
                    }
                }

                int vendorNumFromBaq = baqResult.VendorNum;

                // vendorNum 必须有效：vendorNum<=0 时不能跳过 GetByID 直接走"创建新头"，
                // 否则同一 PackSlip 已存在收货头时会重复建头。
                if (vendorNumFromBaq <= 0)
                {
                    return Fail(0, string.Format(GetText("Receivin", "VendorNumUndetermined"), request.PONum));
                }

                // ============ 调用 GetByID 检查是否已存在收货头 ============
                JObject? existingDs = null;
                var (getByIdResult, getByIdFailed) = await GetByIDAsync(authService, vendorNumFromBaq, "", request.PackSlip);
                // 网络/服务异常时终止流程（不误走"创建新收货头"分支，防同一送货单重复建头）
                if (getByIdFailed)
                    return Fail(0, GetText("Receivin", "QueryReceiptHeadFailed"));
                if (getByIdResult != null)
                {
                    // GetByID 标准返回结构: {"returnObj": {"parameters": {"ds": {...}}}}
                    // 只认显式的 returnObj.parameters.ds；取不到即视为响应结构异常，
                    // 不能用整个信封兜底（否则下游 Set* 在错误结构上静默跳过，按空值过账）。
                    var returnObj = getByIdResult["returnObj"] as JObject;
                    var dsFromReturn = returnObj?["parameters"]?["ds"] as JObject;
                    if (dsFromReturn == null)
                    {
                        _logger.LogError("[{TraceId}] GetByID 响应结构异常（缺少 returnObj.parameters.ds）：PO {PONum}，行 {POLine}，PackSlip {PackSlip}",
                            trId, request.PONum, request.POLine, request.PackSlip);
                        return Fail(0, GetText("Receivin", "InvalidResponseStructure"));
                    }
                    existingDs = dsFromReturn;
                }

                int vendorNumForDtl;
                JObject dsForStep6;
                
                if (existingDs != null)
                {
                    // GetByID 成功返回，跳过 Steps 1-5，直接使用 GetByID 的 ds
                    dsForStep6 = existingDs;
                    vendorNumForDtl = vendorNumFromBaq;

                }
                else
                {
                    // ============ GetByID 没有返回值，执行 Steps 1-5 创建新收货头 ============

                    // Step 1: GetNewRcvHead - 初始化收货头
                    var headResult = await GetNewRcvHeadAsync(authService);
                    if (headResult == null)
                        return Fail(1, string.Format(GetText("Receivin", "InitReceiptHeadFailed"), authService.LastApiError));

                    // Step 2: ValidatePONum - 验证PO号（Epicor BO 验证）
                    var valUrl = "Erp.BO.ReceiptSvc/ValidatePONum";
                    var valPayload = EpicorJsonHelper.Serialize(new { poNum = request.PONum });
                    var valResponseJson = await authService.PostBoJsonAsync(valUrl, valPayload);

                    var (valResponse, valHasError, valErrorMsg) = EpicorJsonHelper.TryParseResponse(valResponseJson);
                    if (valHasError)
                    {
                        return Fail(2, valErrorMsg);
                    }

                    // Epicor BO 标准返回格式: {"returnObj": true/false}
                    var valReturnObj = valResponse?.SelectToken("returnObj");
                    bool valIsValid = false;
                    if (valReturnObj != null)
                    {
                        valIsValid = valReturnObj.ToObject<bool?>() ?? false;
                    }
                    else
                    {
                        var valVal = valResponse?.SelectToken("parameters.valid");
                        if (valVal != null)
                            valIsValid = valVal.ToObject<bool?>() ?? false;
                    }

                    if (!valIsValid)
                    {
                        return Fail(2, string.Format(GetText("Receivin", "PONotReceivable"), request.PONum));
                    }

                    // Step 3: GetPOInfo - 获取PO信息
                    var ds = headResult?["parameters"]?["ds"] ?? headResult;
                    var poInfoResult = await GetPOInfoWithDsAsync(authService, request.PONum, (JObject)ds!);
                    if (poInfoResult == null)
                        return Fail(3, string.Format(GetText("Receivin", "GetPOInfoFailed"), authService.LastApiError));

                    // 从 GetPOInfo 结果获取 VendorNum
                    // 与 BAQ 的 vendorNumFromBaq 分开存放：两者来源不同，
                    //      避免同一变量两次赋值造成语义混淆。
                    int vendorNumFromPoInfo = poInfoResult.VendorNum;
                    if (vendorNumFromPoInfo <= 0)
                        return Fail(3, GetText("Receivin", "GetVendorInfoFailed"));

                    // Step 4: CNPreUpdate - 预更新
                    var poInfoDs = poInfoResult.RawDs;
                    // 设置 PackSlip
                    SetPackSlipInDs(poInfoDs, request.PackSlip);
                    var preUpdateResult = await CNPreUpdateAsync(authService, poInfoDs);
                    if (preUpdateResult == null)
                        return Fail(4, string.Format(GetText("Receivin", "CNPreUpdateFailed"), authService.LastApiError));

                    // Step 5: UpdateMaster - 持久化头
                    var preUpdateDs = preUpdateResult["parameters"]?["ds"] as JObject ?? preUpdateResult;
                    var updateMasterResult = await UpdateMasterAsync(authService, preUpdateDs, vendorNumFromPoInfo, request.PackSlip);
                    if (updateMasterResult == null)
                        return Fail(5, string.Format(GetText("Receivin", "UpdateMasterFailed"), authService.LastApiError));

                    // 从 UpdateMaster 结果获取 ds，用于 Step 6
                    dsForStep6 = updateMasterResult["parameters"]?["ds"] as JObject ?? updateMasterResult;
                    vendorNumForDtl = vendorNumFromPoInfo;
                }

                // Step 6: GetNewRcvDtl - 初始化收货明细
                var dtlResult = await GetNewRcvDtlAsync(authService, vendorNumForDtl, request.PackSlip, dsForStep6);
                if (dtlResult == null)
                    return Fail(6, string.Format(GetText("Receivin", "GetNewRcvDtlFailed"), authService.LastApiError));

                // Step 7: GetDtlPOLineInfo - 获取PO行信息
                var dtlDs = dtlResult["parameters"]?["ds"] as JObject ?? dtlResult;
                var dtlInfoResult = await GetDtlPOLineInfoAsync(authService, vendorNumForDtl, request.PackSlip, request.POLine, dtlDs);
                if (dtlInfoResult == null)
                    return Fail(7, string.Format(GetText("Receivin", "GetDtlPOLineInfoFailed"), authService.LastApiError));

                // Step 8: GetDtlQtyInfo - 设置收货数量
                var dtlInfoDs = dtlInfoResult["parameters"]?["ds"] as JObject ?? dtlInfoResult;
                // 单位一致性兜底：OpenPODetail BAQ 未返回单位列（IUM 为空）时，
                // 按 PO + 行号从 GetDtlPOLineInfo 的 RcvDtl 行唯一定位读取采购单位再比对一次。
                if (string.IsNullOrEmpty(baqResult.IUM))
                {
                    var rcvDtlRecord = FindRcvDtlRecord(dtlInfoDs, request.PONum, request.POLine);
                    var rcvDtlIum = rcvDtlRecord?["IUM"]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(rcvDtlIum) &&
                        !string.Equals(request.Unit, rcvDtlIum, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("收货单位不一致（RcvDtl 兜底）: PONum={PONum}, POLine={POLine}, ScanUnit={ScanUnit}, POUnit={POUnit}",
                            request.PONum, request.POLine, request.Unit, rcvDtlIum);
                        return Fail(7, string.Format(GetText("Receivin", "UnitMismatch"), request.Unit, rcvDtlIum));
                    }
                }
                // 在调用 GetDtlQtyInfo 之前，将 OurQty / InputOurQty、LotNum、PORelNum 和仓库/库位 写入对应 PO/POLine 的 ds 记录
                SetOurQtyInDs(dtlInfoDs, request.PONum, request.POLine, request.Quantity);
                SetLotNumInDs(dtlInfoDs, request.PONum, request.POLine, request.BatchNo);
                SetPORelNumInDs(dtlInfoDs, request.PONum, request.POLine, baqResult.PORelNum);
                SetWarehouseBinInDs(dtlInfoDs, request.PONum, request.POLine, request.WarehouseCode, request.Location);
                var qtyResult = await GetDtlQtyInfoAsync(authService, vendorNumForDtl, request.PackSlip, request.Quantity, dtlInfoDs);
                if (qtyResult == null)
                    return Fail(8, string.Format(GetText("Receivin", "GetDtlQtyInfoFailed"), authService.LastApiError));

                // Step 9: CheckOnLeaveHead - 检查是否可以离开头
                // 必须检查返回值：网络/服务异常时返回 null 并设置 LastApiError，
                // 若不检查则后续 OnChangeDtlReceived 会在脏状态上继续执行。
                var checkHeadResult = await CheckOnLeaveHeadAsync(authService, vendorNumForDtl, request.PackSlip);
                if (checkHeadResult == null)
                    return Fail(9, string.Format(GetText("Receivin", "CheckOnLeaveHeadFailed"), authService.LastApiError));

                // Step 10: OnChangeDtlReceived - 处理收货变化
                var qtyDs = qtyResult["parameters"]?["ds"] as JObject ?? qtyResult;
                // 在调用 OnChangeDtlReceived 之前，设置 ReceivedComplete=true 和 VendorQty/OurQty 到对应 PO/POLine 的记录
                SetReceivedCompleteInDs(qtyDs, request.PONum, request.POLine, false, request.Quantity);
                var onChangeResult = await OnChangeDtlReceivedAsync(authService, vendorNumForDtl, request.PackSlip, qtyDs);
                if (onChangeResult == null)
                    return Fail(10, string.Format(GetText("Receivin", "OnChangeDtlReceivedFailed"), authService.LastApiError));

                // Step 11: CheckDtlLotInfo - 验证批号信息
                var onChangeDs = onChangeResult["parameters"]?["ds"] as JObject ?? onChangeResult;
                var checkLotResult = await CheckDtlLotInfoAsync(authService, vendorNumForDtl, request.PackSlip, request.BatchNo, onChangeDs);
                if (checkLotResult == null)
                    return Fail(11, string.Format(GetText("Receivin", "CheckDtlLotInfoFailed"), authService.LastApiError));
                var checkLotDs = checkLotResult["parameters"]?["ds"] as JObject ?? checkLotResult;
                var checkLotErrorMsg = checkLotResult["parameters"]?["errorMsg"]?.ToString();
                if (!string.IsNullOrEmpty(checkLotErrorMsg))
                    return Fail(11, string.Format(GetText("Receivin", "LotValidationFailed"), checkLotErrorMsg));

                // Step 12: GetDtlLotInfo - 获取和校验批次信息
                var getLotResult = await GetDtlLotInfoAsync(authService, vendorNumForDtl, request.PackSlip, request.BatchNo, checkLotDs);
                if (getLotResult == null)
                    return Fail(12, string.Format(GetText("Receivin", "GetDtlLotInfoFailed"), authService.LastApiError));
                var getLotDs = getLotResult["parameters"]?["ds"] as JObject ?? getLotResult;
                var getLotErrorMsg = getLotResult["parameters"]?["errorMsg"]?.ToString();
                if (!string.IsNullOrEmpty(getLotErrorMsg))
                    return Fail(12, string.Format(GetText("Receivin", "LotInfoValidationFailed"), getLotErrorMsg));

                // Step 13: GetDtlQtyInfo - 获取收货明细数量信息，校验数量限制
                var qtyValidationResult = await GetDtlQtyInfoAsync(authService, vendorNumForDtl, request.PackSlip, request.Quantity, getLotDs);
                if (qtyValidationResult == null)
                    return Fail(13, string.Format(GetText("Receivin", "GetDtlQtyInfoValidationFailed"), authService.LastApiError));
                var qtyValidationDs = qtyValidationResult["parameters"]?["ds"] as JObject ?? qtyValidationResult;
                // Epicor 允许超收：GetDtlQtyInfo 把超收校验放在 warnMsg 出参里返回，
                // Epicor 客户端收到同样的提示后点"是"即继续——它不是硬错误。
                // 记日志放行、文案透传到成功响应，前端收货完成后提示操作员。
                var qtyWarnMsg = qtyValidationResult["parameters"]?["warnMsg"]?.ToString();
                if (!string.IsNullOrEmpty(qtyWarnMsg))
                    _logger.LogWarning("GetDtlQtyInfo 警告放行: {WarnMsg}", qtyWarnMsg);

                // Step 14: CNPreUpdate - 再次预更新
                var preUpdate2Result = await CNPreUpdateAsync(authService, qtyValidationDs);
                if (preUpdate2Result == null)
                    return Fail(14, string.Format(GetText("Receivin", "CNPreUpdateFailed"), authService.LastApiError));

                // Step 15: UpdateMaster - 持久化明细
                var preUpdate2Ds = preUpdate2Result["parameters"]?["ds"] as JObject ?? preUpdate2Result;
                var finalResult = await UpdateMasterAsync(authService, preUpdate2Ds, vendorNumForDtl, request.PackSlip);
                if (finalResult == null)
                {
                    return Fail(15, string.Format(GetText("Receivin", "PersistReceiptDetailFailed"), authService.LastApiError));
                }

                // 15 步全部成功后标记该请求已处理，后续同 requestId 的重复提交将被幂等拦截。
                // Epicor 收货已落地，此处缓存操作失败不应让用户看到"系统异常"而重试，
                // 否则重试时由于没标 processed 可能造成二次收货。包独立 try-catch 仅记日志。
                try
                {
                    _lockProvider.MarkRequestProcessed(idempotentCacheKey, effectiveRequestId);
                }
                catch (Exception markEx)
                {
                    _logger.LogWarning(markEx, "[{TraceId}] MarkRequestProcessed 失败（Epicor 收货已成功，仅幂等标记失败）：RequestId {RequestId}", trId, effectiveRequestId);
                }

                // 单据操作明细：采购收货提交成功
                _documentOperationLogger.Log("采购收货",
                    $"PO{request.PONum}/行{request.POLine}/物料{request.PartNum}/数量{request.Quantity}/送货单{request.PackSlip}",
                    opId, success: true);

                _logger.LogInformation("[{TraceId}] 采购收货成功：用户 {Operator}，PO {PONum}，行 {POLine}，物料 {PartNum}，数量 {Quantity}，仓库 {Whse}/{Bin}，批次 {Batch}，PackSlip {PackSlip}",
                    trId, opId, request.PONum, request.POLine, request.PartNum, request.Quantity,
                    request.WarehouseCode, request.Location, request.BatchNo, request.PackSlip);

                return Json(new
                {
                    success = true,
                    message = GetText("Receivin", "SubmitSuccess"),
                    // 超收等 BO 警告（来自 Step 13 warnMsg），收货已成功，仅作提示
                    warning = string.IsNullOrEmpty(qtyWarnMsg) ? null : qtyWarnMsg,
                    data = new
                    {
                        request.PONum,
                        request.PackSlip,
                        request.PartNum,
                        request.Quantity,
                        request.WarehouseCode,
                        request.Location,
                        request.BatchNo,
                        ReceiptTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    }
                });
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("[{TraceId}] 采购收货失败：会话已过期，用户 {Operator}，PO {PONum}", trId, opId, request?.PONum);
                return Json(new { success = false, message = GetText("Receivin", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{TraceId}] 采购收货异常：用户 {Operator}，PO {PONum}", trId, opId, request?.PONum);

                // 单据操作明细：收货提交异常按失败记录
                _documentOperationLogger.Log("采购收货",
                    $"PO{request?.PONum}/行{request?.POLine}/物料{request?.PartNum}/数量{request?.Quantity}/送货单{request?.PackSlip}",
                    opId, success: false);

                return Json(new { success = false, message = GetText("Receivin", "SystemError") });
            }
            finally
            {
                // 无论成功/失败都立即释放业务锁，避免锁被长期占用
                _lockProvider.ReleaseLock(receiptLock, LockPrefix, lockKey, trId);
            }
        }
        #endregion
    }
}