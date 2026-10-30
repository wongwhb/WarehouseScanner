using System;

namespace WarehouseScannerCore.Models
{
    public class ScanRecord
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Barcode { get; set; } = null!;
        public int ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public int Quantity { get; set; } = 1;
        public string Scanner { get; set; } = null!;
        public string ShelfNumber { get; set; } = null!;
        public DateTime ScanTime { get; set; } = DateTime.Now;
    }
}
