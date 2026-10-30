using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    public class ScanOutboundModel
    {
        [Required(ErrorMessage = "销售订单号不能为空")]
        [StringLength(50, MinimumLength = 5, ErrorMessage = "销售订单号长度应为5-50个字符")]
        [Display(Name = "销售订单号")]
        public string SalesOrderNo { get; set; } = string.Empty;





        [Required(ErrorMessage = "物料编号不能为空")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "物料编号长度应为3-50个字符")]
        [Display(Name = "物料编号")]
        public string MaterialCode { get; set; } = string.Empty;

        [Display(Name = "物料名称")]
        public string MaterialName { get; set; } = string.Empty;

        [Required(ErrorMessage = "数量不能为空")]
        [Range(0.01, 1000000, ErrorMessage = "数量必须大于0")]
        [Display(Name = "数量")]
        public decimal Quantity { get; set; }

        [Display(Name = "单位")]
        public string Unit { get; set; } = string.Empty;

        [Display(Name = "批次号")]
        public string BatchNo { get; set; } = string.Empty;

        [Display(Name = "库位")]
        public string Location { get; set; } = string.Empty;

        [Display(Name = "操作员")]
        public string Operator { get; set; } = string.Empty;

        [Display(Name = "备注")]
        public string Remarks { get; set; } = string.Empty;

        // 扫码相关
        public bool IsScanning { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public bool IsSuccess { get; set; }

        // 历史记录
        public List<OutboundHistory> OutboundHistory { get; set; } = new List<OutboundHistory>();

        // 订单信息
        public OrderInfo OrderInfo { get; set; } = new OrderInfo();
    }

    public class OrderInfo
    {
        public string CustomerName { get; set; } = string.Empty;
        public DateTime OrderDate { get; set; }
        public string OrderStatus { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public decimal ShippedQuantity { get; set; }
        public decimal RemainingQuantity { get; set; }
    }

    public class OutboundHistory
    {
        public DateTime OutboundTime { get; set; }
        public string SalesOrderNo { get; set; } = string.Empty;
        public string MaterialCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string Location { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
    }

    public class OutboundResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public DateTime OutboundTime { get; set; }
    }
}