using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>装箱单号 / 工单条码</summary>
    public partial class OutboundController
    {
        #region 装箱单号
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GeneratePackId()
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";
            try
            {
                // 装箱单号生成是 Epicor 侧写操作（创建 ShipHead），接入并发锁——
                //     与同控制器发货提交的防护标准对齐，防止双击/并发产生两个 PackNum
                string traceId = _lockProvider.GetTraceId(HttpContext);
                string currentUserId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";
                // 锁键带租户（公司|工厂）：业务锁为应用级单例，缺租户会跨站点误阻塞
                string lockKey = $"{CurrentTenantKey}|GeneratePackId";
                SemaphoreSlim? packLock = await _lockProvider.TryAcquireLockAsync(
                    LockPrefix, lockKey, traceId, currentUserId);
                if (packLock == null)
                {
                    return Json(new { success = false, message = GetText("Outbound", "OperationTooFrequent", language) });
                }
                try
                {
                    var authService = CreateEpicorAuthServiceFromSession();
                    var outboundService = new EpicorOutboundService(authService, authService.Company);

                    var shipHeadResponse = await outboundService.GetNewShipHeadAsync();
                    // 必须用 HasApiError 判错：仅凭响应体字段判断会短路放行——PostBoJsonAsync 在 HTTP 非 200 时会设置
                    //     LastApiError 但仍返回响应体，ParseResponse 可能解析成功——错误会以"成功"继续流转
                    if (shipHeadResponse == null || authService.HasApiError)
                        return Json(new { success = false, message = GetText("Outbound", "InitShipHeadFailed", language) });
                    var ds = outboundService.ExtractDs(shipHeadResponse);

                    var updateheadResponse = await outboundService.UpdateHeadMasterAsync(ds);
                    if (updateheadResponse == null || authService.HasApiError)
                        return Json(new { success = false, message = GetText("Outbound", "SaveShipHeadFailed", language) });
                    ds = outboundService.ExtractDs(updateheadResponse);
                    int packNum = outboundService.ExtractPackNum(ds);

                    // ExtractPackNum 解析失败/ShipHead 为空时返回 0——
                    //     必须按失败处理，不能把 0 当成功保存为用户上次 PackID
                    if (packNum <= 0)
                    {
                        _logger.LogWarning("生成Pack ID失败：Epicor 未返回有效 PackNum（ShipHead 缺失或解析失败），操作员 {Operator}", currentUserId);
                        return Json(new { success = false, message = GetText("Outbound", "GeneratePackIdInvalid", language) });
                    }

                    // 保存 packId 到共享文件，下次加载页面自动回填
                    await SaveUserLastPackIdAsync(currentUserId, packNum.ToString());

                    return Json(new { success = true, packNum = packNum });
                }
                finally
                {
                    _lockProvider.ReleaseLock(packLock, LockPrefix, lockKey, traceId);
                }
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("Outbound", "SessionExpired", language) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成Pack ID失败");
                return Json(new { success = false, message = GetText("Outbound", "GeneratePackIdFailedRetry", language) });
            }
        }
        #endregion

        #region PART+PO 条码 → 销售订单 + 库存位置（BAQ SO_SchdUpdate + BAQ Inve_view）
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QueryShippingSchedule([FromBody] QueryShippingScheduleRequest request)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";
            string modeLabel = string.Empty;
            string? partNumForBaq = request?.PartNum?.Trim();
            ShippingScheduleResult? schedule = null;

            try
            {
                // ========== PART:; PO: → 三参模式，调用 BAQ SO_SchdUpdate ==========
                if (request != null && !string.IsNullOrEmpty(request.PONum) && request.OrderLine.HasValue && request.OrderRelNum.HasValue)
                {
                    modeLabel = "PONum/OrderLine/OrderRelNum";

                    var (queriedSchedule, schedApiError) = await _shippingScheduleService.QueryByJobAndPartAsync(
                        request.PONum,
                        request.OrderLine.Value,
                        request.OrderRelNum.Value
                    );

                    // 接口故障与"查无记录"分流：前者透传真实错误，避免前端把它当成"未找到发货计划"
                    // 进而 clearAllFieldsExceptPackId() 清空操作员已录入的字段
                    if (!string.IsNullOrEmpty(schedApiError))
                        return Json(new { success = false, message = string.Format(GetText("Outbound", "EpicorApiFailed", language), schedApiError) });

                    schedule = queriedSchedule;

                    if (schedule == null)
                        return Json(new { success = false, message = string.Format(GetText("Outbound", "ShippingScheduleNotFoundPo", language), request.PONum, request.OrderLine.Value, request.OrderRelNum.Value), code = "ShippingScheduleNotFound" });
                }
                else
                {
                    return Json(new { success = false, message = GetText("Outbound", "InvalidQueryParams", language) });
                }
                // ========== 步骤 2：查 Epicor BAQ Inve_view 获取默认单位/仓库/库位 ==========
                string unit = string.Empty;
                string warehouseCode = string.Empty;
                string binNum = string.Empty;

                List<EpicorAuthService.InventoryModel> lotList = new();

                if (!string.IsNullOrWhiteSpace(partNumForBaq))
                {
                    try
                    {
                        var authService = CreateEpicorAuthServiceFromSession();
                        // 根据物料代码查询库存（不依赖 LotNum/JobNum）
                        lotList = await authService.ListInveViewInventoryByBAQAsync(partNumForBaq, null!);
                    }
                    catch (Exception baqEx)
                    {
                        // 统一失败口径：BAQ 异常与"查无库存"返回同一种失败，不静默降级为成功。
                        // 否则仓库/库位为空会继续流转，导致后续库存校验异常与错误发货。
                        _logger.LogWarning(baqEx, "BAQ Inve_view 查询失败(Mode={Mode}): PartNum={PartNum}", modeLabel, partNumForBaq);
                        return Json(new { success = false, message = GetText("Outbound", "NoStockForPart", language), code = "NoAvailableInventory" });
                    }

                    // 行数不为 0 不代表有库存：BAQ 会返回在手数量为 0 的库存行，
                    // 必须按 Σ 在手数量判"是否有库存"，否则全 0 的料号会被当成"有库存"继续流转
                    if (lotList == null || lotList.Count == 0 || lotList.Sum(x => x.OnhandQty) <= 0)
                        return Json(new { success = false, message = GetText("Outbound", "NoStockForPart", language), code = "NoAvailableInventory" });

                    // 默认单位/仓库/库位取"有在手数量"的首行；lotList 无固定顺序，
                    // 直接取 [0] 可能取到 0 库存行，把默认仓库/库位指向空库位
                    var first = lotList.FirstOrDefault(x => x.OnhandQty > 0) ?? lotList[0];
                    unit = first.IUM;
                    warehouseCode = first.WarehouseCode;
                    binNum = first.BinNum;
                }

                return Json(new
                {
                    success = true,
                    orderNum = schedule.OrderNum,
                    orderLine = schedule.OrderLine,
                    orderRelNum = schedule.OrderRelNum,
                    jobNum = schedule.JobNum ?? string.Empty,
                    planShipQty = schedule.PlanShipQty,
                    shippedQty = schedule.ShippedQty,
                    unit = unit,
                    warehouseCode = warehouseCode,
                    binNum = binNum,
                    lotList = lotList.Select(x => new
                    {
                        lotNum = x.LotNum,
                        firstRefDate = x.FirstRefDate,
                        warehouseCode = x.WarehouseCode,
                        binNum = x.BinNum,
                        unit = x.IUM,
                        onhandQty = x.OnhandQty
                    }).ToArray()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueryShippingSchedule 异常(Mode={Mode}): PartNum={PartNum}, PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum}",
                    modeLabel, request?.PartNum, request?.PONum, request?.OrderLine, request?.OrderRelNum);
                return Json(new { success = false, message = GetText("Outbound", "QueryShippingScheduleFailed", language) });
            }
        }
        #endregion
    }
}