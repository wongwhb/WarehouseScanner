using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Printing;
using System.Drawing.Text;
using WarehouseScannerCore.Models;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 静默打印服务：通过 System.Drawing.Printing 直接把标签送往打印机。
    /// 实现要点：
    ///   1. 已安装字体集合 HashSet 缓存（避免每次 new Font 抛异常的 try/catch）
    ///   2. 同 (familyName, sizeMm, style) 的 Font 实例进程级复用 + GDI Brush 单例
    ///   3. QR 图像按 Base64 内容缓存 → 同物料多份打印不重复解码 PNG
    /// 按 PrintDocument.PrintPage 事件逐页绘制 DrawNormalLabel
    /// </summary>
    public class SilentPrintService
    {
        // ============================================
        // 1. 字体名缓存：首次访问时从 InstalledFontCollection 加载，之后永久缓存
        //    HashSet 查找 O(1)，远优于"try new Font → catch → 判定失败"（~ms 级）
        //    Lazy 延迟到首次打印时构建（此时 _logger 已被 PrintLabels 赋值），
        //    使构建失败的异常能写日志——静态字段初始化期无 logger 可用，异常会被静默吞掉
        // ============================================
        private static ILogger? _logger;

        private static readonly Lazy<HashSet<string>> _installedFontNames = new(BuildInstalledFontSet);

        private static HashSet<string> BuildInstalledFontSet()
        {
            try
            {
                using var ifc = new InstalledFontCollection();
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var family in ifc.Families)
                {
                    set.Add(family.Name);
                    family.Dispose();
                }
                // 兜底：最常用的 Arial 也加进去（极端情况下 InstalledFontCollection 不完整）
                set.Add("Arial");
                return set;
            }
            catch (Exception ex)
            {
                // 任何异常都回退到最小集合，至少保证服务可用。
                // 必须留日志：该回退进程级永久生效，所有非 Arial 标签字体会静默降级，无日志则无法排查。
                _logger?.LogWarning(ex, "读取系统已安装字体列表失败，本进程内标签字体回退 Arial");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Arial" };
            }
        }

        private static bool IsFontInstalled(string fontName)
            => !string.IsNullOrEmpty(fontName) && _installedFontNames.Value.Contains(fontName);

        // ============================================
        // 2. Font / Brush 资源复用
        //    - Brush：全程黑色，进程里只需要一个 SolidBrush(Color.Black)
        //    - Font：按 (名称, 毫米大小, 粗体/常规) 做 ConcurrentDictionary 缓存
        //      注意：缓存的 Font 不 Dispose（进程退出时由 GC 回收），
        //            这样每批次 1000 张相同配置的标签不会 new 1000 个 Font
        // ============================================
        private static readonly Brush _blackBrush = new SolidBrush(Color.Black);

        private readonly struct FontCacheKey : IEquatable<FontCacheKey>
        {
            public readonly string Name;
            public readonly float  SizeMm;
            public readonly FontStyle Style;

            public FontCacheKey(string name, float sizeMm, FontStyle style)
            {
                Name   = name;
                SizeMm = sizeMm;
                Style  = style;
            }

            public bool Equals(FontCacheKey other)
                => string.Equals(Name, other.Name, StringComparison.Ordinal)
                && Math.Abs(SizeMm - other.SizeMm) < 1e-4f
                && Style == other.Style;

            public override bool Equals(object? obj) => obj is FontCacheKey k && Equals(k);

            public override int GetHashCode()
                => HashCode.Combine(Name?.ToLowerInvariant(), SizeMm, (int)Style);
        }

        private static readonly ConcurrentDictionary<FontCacheKey, Font> _fontCache = new();

        private static Font GetOrCreateFont(string familyName, float sizeMm, FontStyle style)
        {
            var key = new FontCacheKey(familyName, sizeMm, style);
            return _fontCache.GetOrAdd(key, k => new Font(k.Name, k.SizeMm, k.Style, GraphicsUnit.Millimeter));
        }

        // ============================================
        // 3. QR 图像 Base64 → Image 缓存（同 Base64 多份打印只解码一次 PNG）
        //    以 LRU 思路限制大小（上限 1024 条），避免长时间运行无限增长
        // ============================================
        private const int MaxQrCacheEntries = 1024;
        private static readonly ConcurrentDictionary<string, Image> _qrImageCache = new();

        // 保护缓存读取、写入与淘汰：淘汰时 Dispose 旧 Image 必须与并发 DrawImage/TryGetValue 互斥，
        // 否则绘制线程可能拿到已释放的 Image 抛 "Parameter is not valid"。
        // DrawNormalLabel 绘制二维码时也持有该锁，保证 Dispose 期间没有并发使用。
        private static readonly object _qrImageCacheLock = new();

        private static Image? GetOrLoadQrImage(string? qrBase64)
        {
            if (string.IsNullOrEmpty(qrBase64)) return null;

            lock (_qrImageCacheLock)
            {
                if (_qrImageCache.TryGetValue(qrBase64, out var cached))
                    return cached;

                try
                {
                    byte[] bytes = Convert.FromBase64String(qrBase64);
                    // GDI+ 约束：Image.FromStream 生成的 Image 在绘制期间仍会读取源流，
                    // 流必须在 Image 整个生命周期内保持打开。故此处不能使用 using / Dispose，
                    // 流与 Image 同生命周期，随缓存条目淘汰时一并释放（缓存上限 1024，内存可控）。
                    var ms = new MemoryStream(bytes);
                    var img = Image.FromStream(ms);

                    // 达到上限时简单地清空缓存（条码标签 QR 尺寸差异很小，清空即可，下一轮会重新建）
                    if (_qrImageCache.Count >= MaxQrCacheEntries)
                    {
                        foreach (var old in _qrImageCache)
                        {
                            _qrImageCache.TryRemove(old.Key, out _);
                            old.Value.Dispose();
                        }
                    }
                    _qrImageCache.TryAdd(qrBase64, img);
                    return img;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "二维码 Base64 解码失败，跳过二维码区域");
                    return null;
                }
            }
        }

        // ============================================
        // 对外入口
        // ============================================
        public static async Task<bool> PrintLabels(List<BarcodePrintModel> labels, string printerName, ILogger logger)
        {
            // 静态工具类的共享日志出口：字体集构建/QR 解码等静态路径也经此输出（Lazy 保证构建发生在本次赋值之后）
            _logger = logger;

            if (labels == null || labels.Count == 0)
            {
                logger?.LogWarning("没有要打印的标签");
                return false;
            }

            try
            {
                using var printDoc = new PrintDocument();
                var firstLabel = labels[0];

                // 设置打印机
                if (!string.IsNullOrEmpty(printerName))
                    printDoc.PrinterSettings.PrinterName = printerName;

                if (!printDoc.PrinterSettings.IsValid)
                {
                    logger?.LogError("打印机无效: {PrinterName}", printerName);
                    return false;
                }

                // 核心：按标签实际尺寸（毫米 → 百分之英寸）设置纸张；强制纵向，禁止旋转
                int paperWidth  = (int)Math.Round(firstLabel.LabelWidth  * 100 / 25.4);
                int paperHeight = (int)Math.Round(firstLabel.LabelHeight * 100 / 25.4);

                printDoc.DefaultPageSettings.PaperSize = new PaperSize("Label", paperWidth, paperHeight);
                printDoc.DefaultPageSettings.Margins   = new Margins(0, 0, 0, 0);
                printDoc.DefaultPageSettings.Landscape = false;

                int index = 0;
                printDoc.PrintPage += (_, e) =>
                {
                    DrawNormalLabel(e.Graphics!, labels[index]);
                    index++;
                    e.HasMorePages = index < labels.Count;
                };

                await Task.Run(() => printDoc.Print());
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "打印异常");
                return false;
            }
        }

        /// <summary>
        /// 单张标签绘制：100% 正常横排，不做任何坐标系旋转
        /// 关键点：
        ///   - 单位 mm（和用户配置一致）
        ///   - 字体 / 画笔 / QR 图像均取自缓存（同一批次 1000 张同配置标签，只创建 1 次 Font/Brush/QR）
        /// </summary>
        private static void DrawNormalLabel(Graphics g, BarcodePrintModel label)
        {
            g.PageUnit = GraphicsUnit.Millimeter;
            g.SmoothingMode      = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint  = TextRenderingHint.ClearTypeGridFit;

            // 文本起始位置（左上角）
            float currentX   = (float)label.PaddingLeft;
            float currentY   = (float)label.PaddingTop;
            float fontSizeMm = (float)label.FontSize * 0.3528f;   // 磅 → 毫米
            float lineHeight = fontSizeMm * 1.3f;

            FontStyle fontStyle = string.Equals(label.FontWeight, "bold", StringComparison.OrdinalIgnoreCase)
                                ? FontStyle.Bold : FontStyle.Regular;
            // 字体回退：请求字体不存在就用 Arial（回退后查一次 HashSet，不抛异常）
            string fontName = IsFontInstalled(label.FontFamily) ? label.FontFamily : "Arial";
            Font   font     = GetOrCreateFont(fontName, fontSizeMm, fontStyle);

            // 文本：按固定横向顺序绘制（字段顺序固定，属业务契约，不得调整）
            string[] lines = new[]
            {
                $"PartNum: {label.MaterialCode ?? ""}",
                $"UOM: {label.Unit ?? ""}",
                FormattableString.Invariant($"Qty: {label.Quantity}"),
                $"Warehouse: {label.Warehouse ?? ""}",
                $"BinNum: {label.Location ?? ""}",
                $"LotNum: {label.BatchNo ?? ""}"
            };

            foreach (var line in lines)
            {
                g.DrawString(line, font, _blackBrush, currentX, currentY);
                currentY += lineHeight;
            }

            // 二维码：固定贴右边对齐，支持 BarcodeOffsetX / Y 像素偏移
            if (!string.IsNullOrEmpty(label.QrCodeBase64))
            {
                try
                {
                    // 与缓存淘汰共用同一把锁：取图与绘制在同一临界区内完成，
                    // 确保淘汰线程 Dispose 时本线程不会正在使用该 Image。
                    lock (_qrImageCacheLock)
                    {
                        var qrImage = GetOrLoadQrImage(label.QrCodeBase64);
                        if (qrImage == null) return;

                        const float pxToMm = 25.4f / 96f;
                        float qrWidth  = label.BarcodeWidth  * pxToMm;
                        float qrHeight = label.BarcodeHeight * pxToMm;

                        float baseX = (float)label.LabelWidth  - qrWidth  - (float)label.PaddingRight;
                        float baseY = (float)label.PaddingTop;

                        float offsetXmm = (float)label.BarcodeOffsetX * pxToMm;
                        float offsetYmm = (float)label.BarcodeOffsetY * pxToMm;

                        // 最终位置 = 右上角基准 + 用户偏移，并裁剪在标签范围内
                        float qrX = Math.Max(0, Math.Min(baseX + offsetXmm, (float)label.LabelWidth  - qrWidth));
                        float qrY = Math.Max(0, Math.Min(baseY + offsetYmm, (float)label.LabelHeight - qrHeight));

                        g.DrawImage(qrImage, qrX, qrY, qrWidth, qrHeight);
                    }
                }
                catch (Exception ex)
                {
                    // 二维码绘制失败不影响整张标签打印（文字仍可打印出来）
                    _logger?.LogWarning(ex, "二维码绘制失败，本张标签跳过二维码");
                }
            }
        }
    }
}
