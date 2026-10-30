namespace WarehouseScannerCore.Models.DTOs
{
    /// <summary>发货请求</summary>
    public class ShipmentRequest
    {
        public string SalesOrderNo { get; set; } = null!;
        public string MaterialCode { get; set; } = null!;
        public string Unit { get; set; } = null!;
        public decimal Quantity { get; set; }
        public string BatchNo { get; set; } = null!;
        public string Location { get; set; } = null!;
        public string WarehouseCode { get; set; } = null!;
        public int? OrderLine { get; set; }
        public int? OrderRelNum { get; set; }
        public string RevisionNum { get; set; } = null!;
        public int? packId { get; set; }
    }

    /// <summary>搜索订单请求</summary>
    public class SearchOrdersRequest
    {
        public string CustomerInput { get; set; } = null!;
        public string CustID { get; set; } = null!;
        public string CustName { get; set; } = null!;
        /// <summary>订单号（Epicor OrderHed.OrderNum）</summary>
        public string OrderNum { get; set; } = null!;
        /// <summary>采购订单号（Epicor OrderHed.PONum）</summary>
        public string PONum { get; set; } = null!;
    }

    /// <summary>验证订单请求</summary>
    public class OutboundValidateOrderRequest
    {
        public string OrderNum { get; set; } = null!;
        public int OrderLine { get; set; }
        public int OrderRelNum { get; set; }
    }

    /// <summary>验证库存请求（发货专用）</summary>
    public class OutboundValidateInventoryRequest
    {
        public string OrderNum { get; set; } = null!;
        public int OrderLine { get; set; }
        public int OrderRelNum { get; set; }
        public string PartNum { get; set; } = null!;
        public string Unit { get; set; } = null!;
        public string WhseCode { get; set; } = null!;
        public string BinNum { get; set; } = null!;
        public string LotNumber { get; set; } = null!;
    }

    /// <summary>ShippingSchedule 查询请求（PART+PO 条码 → 销售订单信息）</summary>
    public class QueryShippingScheduleRequest
    {
        /// <summary>物料号（用于查 BAQ 库存位置）</summary>
        public string PartNum { get; set; } = null!;

        /// <summary>PART+PO 条码场景：PO 号（从 PO:362823-3-1 的首段拆分得到，Epicor OrderHed.PONum 是 varchar）</summary>
        public string PONum { get; set; } = string.Empty;
        /// <summary>PART+PO 条码场景：订单行号（PO:xxx-N-y 的第二段 N）</summary>
        public int? OrderLine { get; set; }
        /// <summary>PART+PO 条码场景：发货批次号（PO:xxx-n-Y 的第三段 Y）</summary>
        public int? OrderRelNum { get; set; }
    }
}
