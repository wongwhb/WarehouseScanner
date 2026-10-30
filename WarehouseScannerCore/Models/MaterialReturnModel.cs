using System;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    public class MaterialReturnModel
    {
        [Required(ErrorMessage = "工单号不能为空")]
        public string WorkOrderNo { get; set; } = null!;

        [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
        public int AssemblySeq { get; set; }

        [Required(ErrorMessage = "物料代码不能为空")]
        public string MaterialCode { get; set; } = null!;

        [Range(0.0001, double.MaxValue, ErrorMessage = "数量必须 > 0")]
        public decimal Quantity { get; set; }

        [Required(ErrorMessage = "单位不能为空")]
        public string Unit { get; set; } = null!;

        public string? FromWarehouse { get; set; }
        public string? Location { get; set; }
        public string? BatchNo { get; set; }
        public string? Remarks { get; set; }
    }

    public class ReturnResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public DateTime ReturnTime { get; set; }
    }
}
