namespace WarehouseScannerCore.Models
{
    public class MaterialIssueRequest
    {
        public string JobNum { get; set; } = null!;          // 工单号
        public int AssemblySeq { get; set; }        // 装配序号，通常为0
        public string MaterialCode { get; set; } = null!;     // 物料代码
        public string Unit{ get; set; } = null!;          // 单位
        public decimal Quantity { get; set; }        // 发料数量
        public int JobSeq { get; set; }              // 工序号（可选，默认为0）
        public string TranType { get; set; } = "STK-MTL";
        public string CallProcess { get; set; } = "IssueMaterial";
    }
}
