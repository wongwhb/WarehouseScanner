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
    /// <summary>订单搜索 / 明细库存查询</summary>
    public partial class OutboundController
    {

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SearchOrders([FromBody] SearchOrdersRequest request)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";
            try
            {
                string custID = (request.CustID ?? "").Trim();
                string custName = (request.CustName ?? "").Trim();
                string orderNum = (request.OrderNum ?? "").Trim();
                string poNum = (request.PONum ?? "").Trim();

                // 至少需要输入一个查询条件
                if (string.IsNullOrWhiteSpace(custID) && string.IsNullOrWhiteSpace(custName)
                    && string.IsNullOrWhiteSpace(orderNum) && string.IsNullOrWhiteSpace(poNum))
                    return Json(new { success = false, message = GetText("Outbound", "PleaseEnterCustomerInfo", language) });

                // 短缓存：同一查询条件 30s 内直接命中缓存、不调 Epicor（用户切换页签/重复点击场景）。
                // 缓存键必须带公司/工厂与操作员：IMemoryCache 为应用级单例，多会话共用同一实例，
                // 只按业务参数作键会让其它公司或其它操作员命中他人的查询结果。
                string operatorId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";
                string cacheKey = $"{CacheKey_SearchOrders}{CacheTenant}|{operatorId}|{custID}|{custName}|{orderNum}|{poNum}";
                if (_memoryCache.TryGetValue(cacheKey, out object? cachedResults))
                {
                    return Json(new { success = true, data = cachedResults });
                }

                var authService = CreateEpicorAuthServiceFromSession();
                var results = await authService.GetCustomerOrderShipments(custID, custName, orderNum, poNum);

                // GetCustomerOrderShipments 在 Epicor 故障时返回空集合（不是 null），必须显式检查错误状态；
                // 否则"接口故障"会被当成"查询成功但没有订单"展示给操作员，并把空结果缓存 30 秒
                if (authService.HasApiError)
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "EpicorApiFailed", language), authService.LastApiError) });

                if (results == null)
                    return Json(new { success = false, message = GetText("Outbound", "SearchFailed", language) });

                // 缓存写入前做 JSON 深拷贝，避免缓存条目与本次返回结果共享可变引用
                var cacheValue = JArray.FromObject(results);
                _memoryCache.Set(cacheKey, cacheValue, TimeSpan.FromSeconds(CacheTtl_SearchOrdersSeconds));
                return Json(new { success = true, data = results });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("Outbound", "SessionExpired", language) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SearchOrders 异常");
                return Json(new { success = false, message = GetText("Outbound", "SearchFailed", language) });
            }
        }

        #region 明细库存查询
        // 库存查询缓存 TTL（秒）：兼顾"仓库扫码员一段时间内反复查看相同物料库存"的典型场景；不改变业务逻辑（只是短时间避免重复 Epicor）
        private const int InventoryCacheSeconds = 30;
        // 批量库存查询并发度：限制同时并发的 Epicor 请求数，避免把 Epicor 接口打爆（不改业务逻辑，只是将串行改为有限并行）
        private const int InventoryBatchConcurrency = 5;
        // 批量库存查询物料数上限：防御异常/恶意客户端提交海量物料导致 Epicor 调用激增与缓存膨胀
        private const int MaxBatchPartNums = 200;

        /// <summary>库存查询缓存键（统一格式：公司 + 物料 + 仓库；批量查询传空仓库，避免两处各自拼接字符串）</summary>
        private static string InvCacheKey(string company, string partNum, string whseCode)
            => $"Outbound:Inv:{company}:{partNum}:{whseCode}";

        /// <summary>
        /// 将 BAQ Inve_view 明细映射为短名对象（wh/bin/lot/qty/u），减少序列化体积。
        /// 单条查询与批量查询共用，避免同一映射逻辑在多处重复。
        /// </summary>
        private static object[] ToShortItems(IReadOnlyList<PartInventoryInfo> inv)
        {
            if (inv == null || inv.Count == 0) return Array.Empty<object>();

            var list = new List<object>(inv.Count);
            foreach (var i in inv)
            {
                list.Add(new
                {
                    wh = i.WhseCode ?? string.Empty,
                    bin = i.BinNum ?? string.Empty,
                    lot = i.LotNumber ?? string.Empty,
                    qty = i.QtyOnHand,
                    u = i.IUM ?? string.Empty
                });
            }
            return list.ToArray();
        }

        /// <summary>
        /// 按物料代码(+仓库)查询 PartBin 库存明细（单条）
        /// —— 结果写入 30s MemoryCache，30s 内同一物料/仓库重复查询零 Epicor 调用。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> QueryInventoryDetail([FromBody] CycleCountInventoryDetailRequest request)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";
            try
            {
                if (string.IsNullOrWhiteSpace(request?.PartNum))
                    return Json(new { success = false, message = GetText("Outbound", "PartNumRequired", language) });

                string partNum = request.PartNum.Trim();
                string whseCode = (request.WarehouseCode ?? "").Trim();

                // 命中 30s 缓存直接返回；未命中才调 Epicor
                var cacheKey = InvCacheKey(CacheTenant, partNum, whseCode);
                if (_memoryCache.TryGetValue(cacheKey, out object[]? cached) && cached != null)
                    return Json(new { success = true, items = cached });

                var inv = await _queryGateway.GetInventoryDetailByInveViewBAQAsync(partNum, whseCode);
                // 接口故障：直接按失败返回，且不写缓存（避免把瞬时故障当"0 库存"缓存 30 秒）
                if (_queryGateway.HasApiError)
                    return Json(new { success = false, message = string.Format(GetText("Outbound", "EpicorApiFailed", language), _queryGateway.LastApiError) });

                // 构造短名对象（wh/bin/lot/qty/u），减少序列化体积；仅成功结果写缓存
                var items = ToShortItems(inv);
                _memoryCache.Set(cacheKey, items, TimeSpan.FromSeconds(InventoryCacheSeconds));

                return Json(new { success = true, items });
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("查询明细库存时会话过期: Part={PartNum}", request?.PartNum);
                return Json(new { success = false, message = GetText("Outbound", "SessionExpired", language) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询发货明细库存异常: Part={PartNum}, Whse={Whse}", request?.PartNum, request?.WarehouseCode);
                // 不透传 ex.Message（可能含主机名/URL 等内部信息），仅返回本地化中性文案
                return Json(new { success = false, message = GetText("Outbound", "SearchFailed", language) });
            }
        }

        /// <summary>
        /// 批量查询多个物料的明细库存。
        /// —— 实现要点：
        ///   1) PartNums 去重 + Trim；
        ///   2) 命中 MemoryCache(30s) 的物料跳过 Epicor；
        ///   3) 未命中物料使用 SemaphoreSlim(N=5) 并发查询，整体耗时 O(N*T) → O(⌈N/5⌉*T)；
        ///   4) 结果字段短名化，减少序列化体积。
        /// —— 按 PartNum 返回明细。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> QueryInventoryDetailBatch([FromBody] BatchInventoryRequest request)
        {
            var language = Request.Cookies["Language"] ?? "zh-CN";
            try
            {
                if (request?.PartNums == null || request.PartNums.Count == 0)
                    return Json(new { success = true, results = new Dictionary<string, object[]>() });

                // 1. 规范化 + 去重（输入中往往存在重复 PartNum）
                var distinctParts = request.PartNums
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 数量上限保护，超出部分截断并告警（前端可通过 truncated 标志感知超限）
                bool truncated = false;
                if (distinctParts.Count > MaxBatchPartNums)
                {
                    _logger.LogWarning("批量查询物料数量超限：去重后 {Count}，上限 {Max}，已截断", distinctParts.Count, MaxBatchPartNums);
                    distinctParts = distinctParts.Take(MaxBatchPartNums).ToList();
                    truncated = true;
                }

                var results = new Dictionary<string, object[]>(distinctParts.Count, StringComparer.OrdinalIgnoreCase);

                // 2. 先扫一遍 MemoryCache，命中的直接填入 results，剩余进入待查集合
                var pending = new List<string>(distinctParts.Count);
                foreach (var partNum in distinctParts)
                {
                    var cacheKey = InvCacheKey(CacheTenant, partNum, "");
                    if (_memoryCache.TryGetValue(cacheKey, out var cached) && cached is object[] arr)
                    {
                        results[partNum] = arr;
                    }
                    else
                    {
                        pending.Add(partNum);
                    }
                }

                // 3. 未命中 → 以 SemaphoreSlim(5) 并发查 Epicor（避免串行化，又不把 Epicor 打爆）
                if (pending.Count > 0)
                {
                    using var sem = new SemaphoreSlim(InventoryBatchConcurrency, InventoryBatchConcurrency);
                    var tasks = new List<Task<(string PartNum, object[] Items, Exception? Error)>>(pending.Count);

                    foreach (var partNum in pending)
                    {
                        tasks.Add(RunInventoryQueryAsync(partNum, sem));
                    }

                    var all = await Task.WhenAll(tasks);

                    foreach (var r in all)
                    {
                        if (r.Error != null)
                        {
                            _logger.LogWarning(r.Error, "批量查询库存单个物料异常: Part={PartNum}", r.PartNum);
                            results[r.PartNum] = new object[0];
                        }
                        else
                        {
                            results[r.PartNum] = r.Items ?? new object[0];
                        }
                    }
                }

                return Json(new { success = true, results, truncated });
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("批量查询明细库存时会话过期");
                return Json(new { success = false, message = GetText("Outbound", "SessionExpired", language) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量查询发货明细库存异常");
                // 不透传 ex.Message（可能含主机名/URL 等内部信息），仅返回本地化中性文案
                return Json(new { success = false, message = GetText("Outbound", "SearchFailed", language) });
            }
        }

        /// <summary>单个库存查询 + 查询结果回写缓存（配合 SemaphoreSlim 并发控制）</summary>
        private async Task<(string PartNum, object[] Items, Exception? Error)> RunInventoryQueryAsync(string partNum, SemaphoreSlim sem)
        {
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                var inv = await _queryGateway.GetInventoryDetailByInveViewBAQAsync(partNum, "").ConfigureAwait(false);

                // 接口故障：不写缓存（避免把瞬时故障当成"0 库存"缓存 30 秒），由调用方记录并把该物料置空
                if (_queryGateway.HasApiError)
                    return (partNum, Array.Empty<object>(), new InvalidOperationException(_queryGateway.LastApiError ?? "Epicor API 调用失败"));

                var items = ToShortItems(inv);

                // 写回 30s 缓存（包括空结果，避免"库存为 0 的物料"反复查 Epicor）
                var cacheKey = InvCacheKey(CacheTenant, partNum, "");
                _memoryCache.Set(cacheKey, items, TimeSpan.FromSeconds(InventoryCacheSeconds));

                return (partNum, items, null);
            }
            catch (Exception ex)
            {
                return (partNum, new object[0], ex);
            }
            finally
            {
                sem.Release();
            }
        }
        #endregion

        #region 批量库存请求 DTO
        public class BatchInventoryRequest
        {
            public List<string> PartNums { get; set; } = new List<string>();
        }
        #endregion
    }
}