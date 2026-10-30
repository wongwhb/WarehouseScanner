using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Linq;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;
using ZXing;

namespace WarehouseScannerCore.Controllers
{
    // 页面权限用特性声明；标签打印归属 LabelPrint 菜单
    [RequirePermission(MenuCodes.LabelPrint)]
    public partial class BarcodeController : BaseController
    {
        // ==============================
        // 缓存键常量（集中管理，避免魔法字符串）
        // ==============================
        private const string CachePrefix_ConfigUser    = "BarcodeCfg:";       // + userName
        private const string CachePrefix_QrCode        = "BarcodeQr:";        // + content|w|h
        private const string CachePrefix_InventoryBAQ  = "BarcodeInvBAQ:";    // + part|wh|bin

        /// <summary>打印份数上限（与 BarcodePrintModel 的 [Range(1,100)] 保持一致），防止恶意提交造成资源耗尽</summary>
        private const int    MaxPrintCopies            = 100;

        private const int    CacheTtl_ConfigSeconds    = 600;   // 配置 10 分钟
        private const int    CacheTtl_QrCodeSeconds    = 3600;  // 二维码 1 小时（同尺寸同内容复用）
        private const int    CacheTtl_InvBAQSeconds    = 30;    // 库存查询 30 秒短缓存

        private readonly string _configPath;

        // ==============================
        // DTO 内部类（保持对外契约不变）
        // ==============================
        public class BarcodePrintItem
        {
            public string PartNum { get; set; } = null!;
            public string PartDescription { get; set; } = null!;
            public string WarehouseCode { get; set; } = null!;
            public string BinNum { get; set; } = null!;
            public string LotNum { get; set; } = null!;
            public string IUM { get; set; } = null!;
            public decimal OnhandQty { get; set; }
        }

        public class DirectPrintRequest
        {
            public string SelectedItemsJson { get; set; } = null!;
            public string PrinterName { get; set; } = null!;
            public double LabelWidth { get; set; }
            public double LabelHeight { get; set; }
            public double PaddingLeft { get; set; }
            public double PaddingRight { get; set; }
            public double PaddingTop { get; set; }
            public double PaddingBottom { get; set; }
            public string FontFamily { get; set; } = null!;
            public int FontSize { get; set; }
            public string FontWeight { get; set; } = null!;
            public int BarcodeWidth { get; set; }
            public int BarcodeHeight { get; set; }
            public int BarcodeOffsetX { get; set; }
            public int BarcodeOffsetY { get; set; }
            /// <summary>打印份数（用户可控输入；越界值在 PrintDirect 入口统一 clamp 到 1~MaxPrintCopies）</summary>
            public int PrintCount { get; set; }

            public DateTime PrintDate { get; set; }
            public int StartNumber { get; set; }
            public int EndNumber { get; set; }
            public bool IsLandscape { get; set; }
            public bool IsFinishedProduct { get; set; }
        }

        public class InventoryQueryRequest
        {
            public string MaterialCode { get; set; } = null!;
            public string Warehouse { get; set; } = null!;
            public string BinNum { get; set; } = null!;
        }

        public BarcodeController(
            ILogger<BarcodeController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IMemoryCache memoryCache,
            ILanguageService languageService,
            UserMenuPermissionService permissionService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _configPath = Path.Combine(_environment.ContentRootPath, "config", "barcodeConfig.json");

            // 确保配置目录存在（应用启动时一次性创建）
            string configDir = Path.GetDirectoryName(_configPath)!;
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
        }

    }
}
