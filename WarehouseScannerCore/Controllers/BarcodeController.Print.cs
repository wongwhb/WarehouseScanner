using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
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
    /// <summary>视图页面 / 打印预览 / 直接打印</summary>
    public partial class BarcodeController
    {
        // 打印列表上限：避免 items.Count × copies 生成海量 Base64 PNG 拖垮内存
        private const int MaxPrintItems = 500;
        private const int MaxTotalLabels = 1000;

        /// <summary>打印条目/总标签数是否超限（copies 已按 MaxPrintCopies 截断）</summary>
        private static bool ExceedsPrintLimit(int itemCount, int copies)
            => itemCount > MaxPrintItems || copies < 1 || copies > MaxPrintCopies
               || (long)itemCount * copies > MaxTotalLabels;

        #region 视图页面

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            try
            {
                // 验证会话有效性：主动获取/刷新 Token（新建实例 _accessToken 为空，必须走真实刷新而非本地判空）
                var epicorService = CreateEpicorAuthServiceFromSession();
                var tokenValid = await epicorService.GetEpicorToken();

                if (!tokenValid)
                {
                    _logger.LogWarning("用户Token获取失败，需要重新登录");
                    return RedirectToAction("Login", "Account");
                }

                // 设置本地化
                SetupViewBagLocalization();

                var model = new BarcodePrintModel();
                await LoadConfigToModel(model);
                return View(model);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "用户未登录或会话已过期");
                return RedirectToAction("Login", "Account");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载条码页面时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }

        #endregion

        #region 打印预览

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Preview(BarcodePrintModel model, string selectedItemsJson)
        {
            try
            {
                var printList = JsonConvert.DeserializeObject<List<BarcodePrintItem>>(selectedItemsJson);
                if (printList == null || printList.Count == 0)
                {
                    _logger.LogWarning("预览请求无可打印物料");
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "SelectItemsFirst") });
                }

                // PrintCount ≤ 1 做兜底保护，避免零份/负数份
                // 份数做上下界兜底（[Range] 保证 1~100，这里再显式截断一次，防绕过）
                int copies = Math.Clamp(model.PrintCount, 1, MaxPrintCopies);
                model.PrintCount = copies;

                // 统一上限校验：条目数 ≤ 500，且实际展开的标签总数（条目×份数）≤ 1000
                if (ExceedsPrintLimit(printList.Count, copies))
                {
                    _logger.LogWarning("预览请求超出打印上限：物料 {ItemCount} 条，份数 {Copies}", printList.Count, copies);
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "PrintLimitExceeded") });
                }

                await SaveUserConfigAsync(model);

                // 构建打印模型集合：按照 PrintCount 展开为多份（业务：用户在界面设置"打印几份"就出几张标签）
                var printModels = ExpandPrintModels(printList, model, copies);

                SetupViewBagLocalization();
                return View("Preview", printModels);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打印预览失败");
                return Json(new { success = false, message = GetLocalText("BarcodePrint", "PreviewError") });
            }
        }

        #endregion

        #region 直接打印

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PrintDirect([FromBody] DirectPrintRequest request)
        {
            try
            {
                // ===== 1. 入口校验（ModelState 最先执行，再执行业务参数校验） =====
                // PrintDirect 非 [ApiController] 路由，不会自动拦截模型绑定错误 → 显式检查一次
                if (!ModelState.IsValid)
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "InvalidRequest") });

                if (request == null || string.IsNullOrEmpty(request.SelectedItemsJson))
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "InvalidRequest") });
                if (string.IsNullOrEmpty(request.PrinterName))
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "PrinterRequired") });

                var printList = JsonConvert.DeserializeObject<List<BarcodePrintItem>>(request.SelectedItemsJson);
                if (printList == null || printList.Count == 0)
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "SelectItemsFirst") });

                // ===== 2. 组装配置 DTO + 持久化用户配置 =====
                var configModel = new BarcodePrintModel
                {
                    LabelWidth     = request.LabelWidth,
                    LabelHeight    = request.LabelHeight,
                    PaddingLeft    = request.PaddingLeft,
                    PaddingRight   = request.PaddingRight,
                    PaddingTop     = request.PaddingTop,
                    PaddingBottom  = request.PaddingBottom,
                    FontFamily     = request.FontFamily,
                    FontSize       = request.FontSize,
                    FontWeight     = request.FontWeight,
                    BarcodeWidth   = request.BarcodeWidth,
                    BarcodeHeight  = request.BarcodeHeight,
                    BarcodeOffsetX = request.BarcodeOffsetX,
                    BarcodeOffsetY = request.BarcodeOffsetY,
                    PrintCount     = request.PrintCount,
                    PrintDate      = request.PrintDate,
                    StartNumber    = request.StartNumber,
                    EndNumber      = request.EndNumber,
                    IsLandscape    = request.IsLandscape,
                    PrintName      = request.PrinterName
                };
                // 直打路径份数上限统一走 clamp（与 Preview 一致）：低于 1 或高于上限都截断，
                // 不在此处拒绝，避免同一字段一处拒绝一处 clamp。
                int copies = Math.Clamp(configModel.PrintCount, 1, MaxPrintCopies);
                configModel.PrintCount = copies;

                // 统一上限校验：条目数 ≤ 500，且实际展开的标签总数（条目×份数）≤ 1000
                if (ExceedsPrintLimit(printList.Count, copies))
                {
                    _logger.LogWarning("直接打印请求超出上限：物料 {ItemCount} 条，份数 {Copies}", printList.Count, copies);
                    return Json(new { success = false, message = GetLocalText("BarcodePrint", "PrintLimitExceeded") });
                }

                await SaveUserConfigAsync(configModel);

                // ===== 3. 按 PrintCount 展开打印模型（同预览入口，避免逻辑分叉） =====
                var printModels = ExpandPrintModels(printList, configModel, copies);

                // ===== 4. 调用静默打印 =====
                bool printSuccess = await SilentPrintService.PrintLabels(printModels, request.PrinterName, _logger);
                if (!printSuccess)
                {
                    _logger.LogError("直接打印失败：静默打印服务返回 false，打印机 {PrinterName}", request.PrinterName);
                }
                return printSuccess
                    ? Json(new { success = true,  message = GetLocalText("BarcodePrint", "PrintJobSent") })
                    : Json(new { success = false, message = GetLocalText("BarcodePrint", "PrintFailedCheckPrinter") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "直接打印异常");
                return Json(new { success = false, message = GetLocalText("BarcodePrint", "PrintError") });
            }
        }

        #endregion
    }
}