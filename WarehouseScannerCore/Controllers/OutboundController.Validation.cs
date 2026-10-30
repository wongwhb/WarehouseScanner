﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using Microsoft.AspNetCore.DataProtection;
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
    /// <summary>验证接口</summary>
    public partial class OutboundController
    {
        #region 验证接口

        /// <summary>
        /// 共用：查询订单行（OrderNum + OrderLine → 一组 OrderRel），带 30 秒短缓存
        /// 说明：ValidateOrder 与 ValidateInventory 第一步都要查相同的订单行集合，
        ///       提取后避免了两次相同 Epicor 查询。
        ///       返回强类型 EpicorAuthService.OrderRelInfo。
        ///       <paramref name="apiError"/> 非空表示 Epicor 接口故障（此时 list 为空），调用方必须按"接口失败"提示，
        ///       不能把故障当成"订单不存在"。
        /// </summary>
        private async Task<(IReadOnlyList<EpicorAuthService.OrderRelInfo> list, string? apiError)> GetOrderRelsCachedAsync(string orderNum, int orderLine)
        {
            // 缓存键带公司代码：多公司会话共用同一 IMemoryCache，只按订单号作键会互相命中
            string cacheKey = $"{CacheKey_OrderRels}{CacheTenant}|{orderNum}|{orderLine}";
            if (_memoryCache.TryGetValue(cacheKey, out IReadOnlyList<EpicorAuthService.OrderRelInfo>? cached) && cached != null)
                return (cached, null);

            var authService = CreateEpicorAuthServiceFromSession();
            var rawList = await authService.QueryOrderRelsBySalesOrder(orderNum, orderLine);

            // Epicor 故障（HasApiError=true）时不写缓存（避免把"故障"误当作"无数据"缓存 30 秒），
            // 并把错误原文回传调用方——否则下发 Count==0 会被误判为"未找到订单"。
            if (authService.HasApiError)
                return (Array.Empty<EpicorAuthService.OrderRelInfo>(), authService.LastApiError ?? GetText("Outbound", "UnknownError"));

            var list = rawList != null
                ? rawList.AsReadOnly()
                : new List<EpicorAuthService.OrderRelInfo>().AsReadOnly();
            _memoryCache.Set(cacheKey, list, TimeSpan.FromSeconds(CacheTtl_OrderRelsSeconds));
            return (list, null);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidateOrder([FromBody] OutboundValidateOrderRequest request)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";
            try
            {
                // 复用缓存后的订单行查询：避免与 ValidateInventory 产生重复 Epicor 调用
                var (shipmentList, apiError) = await GetOrderRelsCachedAsync(request.OrderNum, request.OrderLine);
                if (apiError != null)
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "EpicorApiFailed", language), apiError) });

                var shipmentDetail = shipmentList.FirstOrDefault(x => x.OrderRelNum == request.OrderRelNum);

                if (shipmentDetail == null)
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "OrderNotFound", language), request.OrderNum, request.OrderLine) });

                // Epicor 布尔字段大小写不固定，统一复用业务服务的 IsTrueFlag（兼容 True/true/1），
                // 避免与 OutboundBusinessService 出现两套判定
                if (!OutboundBusinessService.IsTrueFlag(shipmentDetail.ShipLocked_c))
                    return Json(new { success = false, message = GetText("Outbound", "OrderLocked", language) });

                if (!OutboundBusinessService.IsTrueFlag(shipmentDetail.FirmRelease))
                    return Json(new { success = false, message = GetText("Outbound", "FirmRelease", language) });

                // #region 业务 - 订单状态校验（兼容 Epicor 英文 Open / 中文 未结）
                if (!string.Equals(shipmentDetail.Status, "Open", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(shipmentDetail.Status, "未结", StringComparison.Ordinal))
                    return Json(new { success = false, message = GetText("Outbound", "OrderStatus", language) });

                decimal outboundQty = shipmentDetail.OrderQty - shipmentDetail.TotalJobStockShipped;
                if (outboundQty <= 0)
                    return Json(new { success = false, message = GetText("Outbound", "OrderFullyShipped", language) });

                // 验证通过（数量由前端根据 QueryShippingSchedule/ValidateInventory 的返回值渲染，
                // 此处不回传 orderQty/totalJobStockShipped/sellingRemainQty 等前端不读取的字段）
                return Json(new
                {
                    success = true,
                    message = GetText("Outbound", "OrderValidationPassed", language)
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("Outbound", "SessionExpired", language) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证订单出错");
                return Json(new { success = false, message = GetText("Outbound", "OrderValidationFailed", language) });
            }
        }

        /// <summary>
        /// 库存校验接口（实为"查询并回填"）：按 物料+仓库+库位+批次 调 BAQ Inve_view 取库存数回传前端，
        /// 本接口不做数量拦截（无库存/超量在提交时由 OutboundBusinessService 按同一 BAQ 口径处理）。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidateInventory([FromBody] OutboundValidateInventoryRequest request)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";
            try
            {
                // 订单行查询：优先命中上面 ValidateOrder 写入的短缓存，减少一次 Epicor 往返
                var (shipmentList, apiError) = await GetOrderRelsCachedAsync(request.OrderNum, request.OrderLine);
                if (apiError != null)
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "EpicorApiFailed", language), apiError) });

                var shipmentDetail = shipmentList.FirstOrDefault(x => x.OrderRelNum == request.OrderRelNum);

                if (shipmentDetail == null)
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "OrderNotFound", language), request.OrderNum, request.OrderLine) });

                if (shipmentDetail.PartNum != request.PartNum)
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "MaterialMismatch", language), request.PartNum, shipmentDetail.PartNum) });

                // 扫码解析阶段不校验批号/库位为空（多条库存时需手工选择批号），
                // 批号/库位的必填校验由提交时前端 validateForm + SubmitShipment 兜底执行。
                // 批号/库位为空时该维度不参与 BAQ 过滤，按物料（+仓库）汇总库存数。

                // 单位为空时跳过比对（多条库存场景下单位可能尚未回填），提交时仍会做最终校验
                if (!string.IsNullOrEmpty(request.Unit) &&
                    !string.Equals(shipmentDetail.IUM, request.Unit, StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "UnitMismatch", language), request.Unit, shipmentDetail.IUM) });

                // 库存查询：调 BAQ Inve_view，入参为 物料代码 + 仓库代码 + 库位 + 批次号（前端文本框的值），
                // 与批号单选框（lotList）同一数据源，避免走 PartBinSearch 过滤维度不一致导致"有库存却提示无库存"。
                var inventoryList = await _queryGateway.GetInventoryDetailByInveViewBAQAsync(
                    request.PartNum, request.WhseCode, request.BinNum, request.LotNumber);

                if (_queryGateway.HasApiError)
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "EpicorApiFailed", language), _queryGateway.LastApiError) });

                // 库存数 = Σ 在手数量（BAQ Inve_view 不返回已分配数量）；本接口只把库存数回传前端显示，
                // 不做数量校验（不拦截超量/无库存）。JSON 字段名沿用 availableQty，前端按"库存数"展示。
                decimal availableQty = inventoryList?.Sum(x => x.QtyOnHand) ?? 0;

                // 回传所选批号的库存数，供前端在"发货数量"下方显示"库存数"
                return Json(new { success = true, message = GetText("Outbound", "InventoryValidationPassed", language), availableQty, lotNumber = request.LotNumber ?? string.Empty });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("Outbound", "SessionExpired", language) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证库存出错");
                return Json(new { success = false, message = GetText("Outbound", "InventoryValidationFailed", language) });
            }
        }

        #endregion
    }
}