using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    /// <summary>
    /// 条码打印配置与内容模型
    /// </summary>
    public class BarcodePrintModel
    {
        public string QrCodeBase64 { get; set; } = null!;

        // ========== 打印内容字段 ==========
        [Display(Name = "物料代码")]
        public string MaterialCode { get; set; } = null!;

        [Display(Name = "单位")]
        public string Unit { get; set; } = null!;

        [Display(Name = "数量")]
        public decimal Quantity { get; set; }

        [Display(Name = "仓库")]
        public string Warehouse { get; set; } = null!;

        [Display(Name = "库位")]
        public string Location { get; set; } = null!;

        [Display(Name = "批号")]
        public string BatchNo { get; set; } = null!;

        [Display(Name = "打印数量")]
        [Range(1, 100, ErrorMessage = "打印数量需在1-100之间")]
        public int PrintCount { get; set; } = 1;

        [Display(Name = "开始数量")]
        public int? StartNumber { get; set; }

        [Display(Name = "结束数量")]
        public int? EndNumber { get; set; }

        [Display(Name = "打印日期")]
        [DataType(DataType.Date)]
        public DateTime PrintDate { get; set; } = DateTime.Today;

        // ========== 打印方向设置 ==========
        [Display(Name = "横向打印")]
        public bool IsLandscape { get; set; } = false;
        // ========== 打印机名称 ==========
        public string  PrintName { get; set; } = null!;

        // ========== 标签类型：成品 ==========
        [Display(Name = "成品")]
        public bool IsFinishedProduct { get; set; } = false;


        // ========== 批量选中的物料列表 ==========
        public BarcodePrintModel()
        {
            SelectedItems = new List<InventoryModel>();
        }

        public List<InventoryModel> SelectedItems { get; set; }

        // ========== 标签尺寸（毫米） ==========
        [Display(Name = "标签宽度 (mm)")]
        [Range(20, 200)]
        public double LabelWidth { get; set; } = 60;

        [Display(Name = "标签高度 (mm)")]
        [Range(20, 150)]
        public double LabelHeight { get; set; } = 40;

        // ========== 内边距（毫米） ==========
        [Display(Name = "左边距 (mm)")]
        public double PaddingLeft { get; set; } = 2;

        [Display(Name = "右边距 (mm)")]
        public double PaddingRight { get; set; } = 2;

        [Display(Name = "上边距 (mm)")]
        public double PaddingTop { get; set; } = 2;

        [Display(Name = "下边距 (mm)")]
        public double PaddingBottom { get; set; } = 2;

        // ========== 字体设置 ==========
        [Display(Name = "字体名称")]
        public string FontFamily { get; set; } = "Arial";

        [Display(Name = "字体大小 (pt)")]
        [Range(6, 72)]
        public int FontSize { get; set; } = 9;

        [Display(Name = "字体粗细")]
        public string FontWeight { get; set; } = "normal";

        // ========== 条码图片设置 ==========
        [Display(Name = "条码宽度 (px)")]
        [Range(20, 500)]
        public int BarcodeWidth { get; set; } = 120;

        [Display(Name = "条码高度 (px)")]
        [Range(20, 500)]
        public int BarcodeHeight { get; set; } = 120;

        [Display(Name = "水平偏移 (px)")]
        public int BarcodeOffsetX { get; set; } = 0;

        [Display(Name = "垂直偏移 (px)")]
        public int BarcodeOffsetY { get; set; } = 0;
    }

    public class InventoryModel
    {
        public string PartNum { get; set; } = null!;
        public string PartDescription { get; set; } = null!;
        public string WarehouseCode { get; set; } = null!;
        public string BinNum { get; set; } = null!;
        public string LotNum { get; set; } = null!;
        public string IUM { get; set; } = null!;
        public decimal OnhandQty { get; set; }
    }
}
