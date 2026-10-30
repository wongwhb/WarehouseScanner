using System;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models.DTOs
{
    #region 工单入库 - 查询装配序号请求
    /// <summary>
    /// 根据工单号+物料号查询 AssemblySeq
    /// </summary>
    public class GetAssemblySeqRequest
    {
        [Required(ErrorMessage = "工单号不能为空")]
        public string JobNum { get; set; } = string.Empty;

        [Required(ErrorMessage = "物料代码不能为空")]
        public string PartNum { get; set; } = string.Empty;
    }
    #endregion

    #region 工单入库 - 工单验证请求
    /// <summary>
    /// 工单入库 - 工单状态验证请求 DTO
    /// </summary>
    public class ValidateReceiptJobRequest
    {
        [Required(ErrorMessage = "工单号不能为空")]
        public string WorkOrderNo { get; set; } = string.Empty;

        [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
        public int AssemblySeq { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "扫描数量必须 >= 0")]
        public decimal ScanQty { get; set; }

        public string MaterialCode { get; set; } = string.Empty;
    }
    #endregion

    #region 工单入库 - 物料验证请求
    /// <summary>
    /// 工单入库 - 物料验证请求 DTO
    /// </summary>
    public class ValidateReceiptMaterialRequest
    {
        [Required(ErrorMessage = "工单号不能为空")]
        public string WorkOrderNo { get; set; } = string.Empty;

        [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
        public int AssemblySeq { get; set; }

        [Required(ErrorMessage = "物料代码不能为空")]
        public string MaterialCode { get; set; } = string.Empty;

        [Range(0.0001, double.MaxValue, ErrorMessage = "数量必须 > 0")]
        public decimal Quantity { get; set; }
    }
    #endregion

    #region 工单入库请求
    /// <summary>
    /// 工单入库请求 DTO
    /// </summary>
    public class ReceiveMfgPartRequest
    {
        [Required(ErrorMessage = "工单号不能为空")]
        public string WorkOrderNo { get; set; } = string.Empty;

        [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
        public int AssemblySeq { get; set; }

        [Required(ErrorMessage = "物料代码不能为空")]
        public string MaterialCode { get; set; } = string.Empty;

        [Range(0.0001, double.MaxValue, ErrorMessage = "数量必须 > 0")]
        public decimal Quantity { get; set; }

        public string? Unit { get; set; }

        [Required(ErrorMessage = "仓库不能为空")]
        public string? WarehouseCode { get; set; }

        [Required(ErrorMessage = "库位不能为空")]
        public string? Location { get; set; }

        [Required(ErrorMessage = "批次号不能为空")]
        public string? BatchNo { get; set; }
    }
    #endregion

    #region 工单入库结果响应
    /// <summary>
    /// 工单入库结果响应 DTO
    /// </summary>
    public class ReceiveMfgPartResult
    {
        public string WorkOrderNo { get; set; } = string.Empty;
        public int AssemblySeq { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string? BatchNo { get; set; }
        public string? Location { get; set; }
        public string Operator { get; set; } = string.Empty;
        public string ReceiptTime { get; set; } = string.Empty;
        public string? TranNum { get; set; }
        /// <summary>Epicor BO 返回的警告信息（如超收提示），非空表示入库成功但有业务警告</summary>
        public string? WarningMessage { get; set; }
    }
    #endregion

    #region 工单入库 - 工单状态信息
    /// <summary>
    /// 工单入库 - 工单状态信息 DTO
    /// </summary>
    public class ReceiptJobStatusInfo
    {
        public string JobNum { get; set; } = string.Empty;
        public string PartNum { get; set; } = string.Empty;
        public bool JobFirm { get; set; }
        public bool JobClosed { get; set; }
        public decimal ProdQty { get; set; }
        public decimal ReceivedQty { get; set; }
        public decimal AvailableQty => ProdQty - ReceivedQty;
        /// <summary>库存数量（物料实际在手库存汇总）</summary>
        public decimal StockQty { get; set; }
        public string WarehouseCode { get; set; } = string.Empty;
        public string BinNum { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
    #endregion

    #region 工单入库 - 物料信息
    /// <summary>
    /// 工单入库 - 物料信息 DTO
    /// </summary>
    public class ReceiptMaterialInfo
    {
        public string PartNum { get; set; } = string.Empty;
        public string PartDesc { get; set; } = string.Empty;
        public string IUM { get; set; } = string.Empty;
        public decimal QtyAvailToComplete { get; set; }
        public decimal QtyCompleted { get; set; }
        public decimal AvailableQty => QtyAvailToComplete - QtyCompleted;
    }
    #endregion
}