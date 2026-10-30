using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;
using ZXing;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>库存查询 / 打印机管理</summary>
    public partial class BarcodeController
    {
        #region 库存查询

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GetInventoryList([FromBody] InventoryQueryRequest request)
        {
            try
            {
                EpicorAuthService epicorService;
                try
                {
                    epicorService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogWarning("【库存查询】登录已过期");
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "SessionExpired") });
                }

                request ??= new InventoryQueryRequest();
                string materialCode  = request.MaterialCode?.Trim() ?? "";
                string warehouseCode = request.Warehouse?.Trim()      ?? "";
                string binNum        = request.BinNum?.Trim()         ?? "";

                // ===== 短缓存（30s）：相同条件重复查询零 Epicor 调用 =====
                // 键必须带租户（公司|工厂）：应用级单例缓存跨站点共享，无租户会串库存数据
                string tenant = CurrentTenantKey;
                string cacheKey = $"{CachePrefix_InventoryBAQ}{tenant}|{materialCode}|{warehouseCode}|{binNum}";
                if (!_memoryCache.TryGetValue(cacheKey, out IReadOnlyList<dynamic>? inventoryList))
                {
                    // BAQ 端已按 物料/仓库/库位 过滤，这里不做重复 LINQ 过滤，避免双重过滤的冗余
                    var raw = await epicorService.ListInveViewInventoryByBAQAsync(
                        partNum: materialCode,
                        warehouseCode: warehouseCode,
                        binNum: binNum);
                    inventoryList = raw != null ? raw.Cast<dynamic>().ToList().AsReadOnly()
                                                : (IReadOnlyList<dynamic>)Array.Empty<dynamic>();
                    _memoryCache.Set(cacheKey, inventoryList,
                        TimeSpan.FromSeconds(CacheTtl_InvBAQSeconds));
                }

                if (inventoryList == null || inventoryList.Count == 0)
                {
                    _logger.LogWarning("【库存查询】无数据");
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "NoInventoryData") });
                }

                // 只做长度上限保护，不重复过滤（BAQ 已经完成过滤职责）
                var finalList = inventoryList.Take(500).ToList();

                // 短字段投影（和前端约定的字段名一致，减少 JSON 传输体积）
                var result = finalList.Select(item => new
                {
                    partNum         = (string)item.PartNum,
                    partDescription = (string)item.PartDescription,
                    warehouseCode   = (string)item.WarehouseCode,
                    binNum          = (string)item.BinNum,
                    lotNum          = (string)item.LotNum,
                    ium             = (string)item.IUM,
                    onhandQty       = (decimal)item.OnhandQty
                }).ToList();

                return Json(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【库存查询】异常");
                return Json(new { success = false, message = GetLocalText("BarcodePrint", "ServiceError") });
            }
        }

        #endregion

        #region 打印机管理

        [HttpGet]
        public IActionResult GetSystemPrinters()
        {
            try
            {
                // PrinterSettings.InstalledPrinters 本身就是字符串集合，直接 ToList() 即可
                var printers = System.Drawing.Printing.PrinterSettings.InstalledPrinters
                    .Cast<string>()
                    .ToList();
                return Json(printers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取打印机列表失败");
                return Json(new List<string>());
            }
        }

        #endregion
    }
}