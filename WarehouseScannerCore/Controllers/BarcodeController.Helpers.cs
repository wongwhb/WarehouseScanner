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
    /// <summary>辅助方法</summary>
    public partial class BarcodeController
    {
        #region 辅助方法

        /// <summary>
        /// 把物料清单 × 打印份数 展开为最终打印模型集合（预览/直接打印共用，保证行为一致）
        ///   - 每份生成独立的 QR（内容含 Serial:startNumber..endNumber 则逐份递增，否则相同）
        ///   - QR 按"内容+尺寸"做 1 小时缓存，重复标签零 ZXing 编码开销
        /// </summary>
        private List<BarcodePrintModel> ExpandPrintModels(
            IReadOnlyList<BarcodePrintItem> items, BarcodePrintModel config, int copies)
        {
            var printModels = new List<BarcodePrintModel>(items.Count * copies);
            int start = config.StartNumber ?? 0;
            int end   = config.EndNumber   ?? 0;
            bool hasSerial = start > 0 && end >= start;

            foreach (var item in items)
            {
                for (int c = 0; c < copies; c++)
                {
                    var m = CreatePrintModel(config, item);

                    // 若设置了 StartNumber..EndNumber，按份 +1；未设置则不传 Serial，保持 QR 内容与旧逻辑一致
                    int? serial = null;
                    if (hasSerial)
                        serial = Math.Min(start + c, end);

                    string qrContent = BuildQrContent(m, serial);
                    m.QrCodeBase64 = GenerateSingleQrCodeCached(qrContent, m.BarcodeWidth, m.BarcodeHeight);
                    printModels.Add(m);
                }
            }
            return printModels;
        }

        /// <summary>
        /// 创建单个标签打印模型（配置字段 + 物料数据映射）
        /// 注意：QR 不在此处生成（因为 QR 与 copies/serial 相关，由外层负责）
        /// </summary>
        private static BarcodePrintModel CreatePrintModel(BarcodePrintModel config, BarcodePrintItem item)
            => new()
            {
                LabelWidth     = config.LabelWidth,
                LabelHeight    = config.LabelHeight,
                PaddingLeft    = config.PaddingLeft,
                PaddingRight   = config.PaddingRight,
                PaddingTop     = config.PaddingTop,
                PaddingBottom  = config.PaddingBottom,
                FontFamily     = config.FontFamily,
                FontSize       = config.FontSize,
                FontWeight     = config.FontWeight,
                BarcodeWidth   = config.BarcodeWidth,
                BarcodeHeight  = config.BarcodeHeight,
                BarcodeOffsetX = config.BarcodeOffsetX,
                BarcodeOffsetY = config.BarcodeOffsetY,
                PrintCount     = config.PrintCount,
                PrintDate      = config.PrintDate,
                StartNumber    = config.StartNumber,
                EndNumber      = config.EndNumber,
                IsLandscape    = config.IsLandscape,
                IsFinishedProduct = config.IsFinishedProduct,
                PrintName      = config.PrintName,
                MaterialCode   = item.PartNum,
                Quantity       = item.OnhandQty,
                Unit           = item.IUM,
                Warehouse      = item.WarehouseCode,
                Location       = item.BinNum,
                BatchNo        = item.LotNum,
            };

        private static string BuildQrContent(BarcodePrintModel model, int? serialNumber = null)
        {
            var parts = new List<string>(7)
            {
                $"PartNum:{model.MaterialCode}",
                FormattableString.Invariant($"Qty:{model.Quantity}"),
                $"UOM:{model.Unit}",
            };
            if (!model.IsFinishedProduct)
            {
                parts.Add($"WarehouseDesc:{model.Warehouse}");
                parts.Add($"BinNum:{model.Location}");
            }
            parts.Add($"LotNum:{model.BatchNo}");
            return string.Join(";", parts);
        }

        /// <summary>
        /// 带缓存的二维码生成：
        ///   - 缓存键 = 内容 + 宽 + 高（完全相同的 QR 1 小时内不会重复编码与位图转换）
        ///   - 典型场景：同一物料打印 N 份（未启用序号）→ 只做 1 次 ZXing 编码
        /// </summary>
        private string GenerateSingleQrCodeCached(string content, int width, int height)
        {
            string cacheKey = $"{CachePrefix_QrCode}{content}|{width}|{height}";
            if (_memoryCache.TryGetValue(cacheKey, out string? base64) && !string.IsNullOrEmpty(base64))
                return base64;

            base64 = GenerateSingleQrCode(content, width, height);
            _memoryCache.Set(cacheKey, base64, TimeSpan.FromSeconds(CacheTtl_QrCodeSeconds));
            return base64;
        }

        /// <summary>纯 ZXing 编码 + PNG → Base64，不含任何缓存逻辑，单独可测</summary>
        private static string GenerateSingleQrCode(string content, int width, int height)
        {
            var writer = new ZXing.BarcodeWriterPixelData
            {
                Format = ZXing.BarcodeFormat.QR_CODE,
                Options = new ZXing.QrCode.QrCodeEncodingOptions
                {
                    Width = width,
                    Height = height,
                    Margin = 1,
                    CharacterSet = "UTF-8"
                }
            };

            var pixelData = writer.Write(content);
            using var bitmap = new System.Drawing.Bitmap(pixelData.Width, pixelData.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var ms = new System.IO.MemoryStream();

            var bitmapData = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, pixelData.Width, pixelData.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                bitmap.PixelFormat);
            System.Runtime.InteropServices.Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
            bitmap.UnlockBits(bitmapData);
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }

        #endregion
    }
}
