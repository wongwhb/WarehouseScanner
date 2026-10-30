using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    public class MaterialIssueModel
    {
        public string WorkOrderNo { get; set; } = null!;
        public string MaterialCode { get; set; } = null!;
        public int AssemblySeq { get; set; }
        public decimal Quantity { get; set; }
        public string Unit { get; set; } = null!;
        public string FromWarehouse { get; set; } = null!;
        public string BatchNo { get; set; } = null!;
        public string Location { get; set; } = null!;
        public string Remarks { get; set; } = null!;
       
    }

    public class MaterialInfo
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string BatchNo { get; set; } = string.Empty;
        public string Warehouse { get; set; } = string.Empty;
        public decimal StockQuantity { get; set; }
    }


    public class IssueResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public DateTime IssueTime { get; set; }
    }
}
