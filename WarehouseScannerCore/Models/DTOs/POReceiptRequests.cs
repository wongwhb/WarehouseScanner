using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models.DTOs
{
    #region 采购收货 - PO 验证请求

    /// <summary>
    /// 采购收货 - PO 验证请求 DTO
    /// </summary>
    public class ValidatePORequest
    {
        [Required(ErrorMessage = "采购订单号不能为空")]
        public string PONum { get; set; } = string.Empty;
        public int POLine { get; set; }
        public string PartNum { get; set; } = string.Empty;
        public decimal Quantity { get; set; }

        /// <summary>
        /// 扫码单位。渐进式扫码（先扫 PO 后扫物料）时可能为空，
        /// 为空时 ValidatePO 跳过单位比对，交由提交侧必填兜底。
        /// </summary>
        public string Unit { get; set; } = string.Empty;

        public string WarehouseCode { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
    }

    #endregion

    #region 采购收货 - 完成收货请求

    /// <summary>
    /// 采购收货 - 完成收货请求 DTO
    /// </summary>
    public class CompleteReceiptRequest
    {
        /// <summary>
        /// 请求幂等标识。
        /// 前端每次提交生成一个唯一值，重复提交（网络重试 / 双击 / 扫码枪重复触发）时
        /// 服务端凭此直接返回上一次的成功结果，不会重复收货。
        /// 也可通过 X-Request-ID 请求头传递（两者取其一，请求头优先）。
        /// </summary>
        public string RequestId { get; set; } = string.Empty;

        [Required(ErrorMessage = "采购订单号不能为空")]
        public string PONum { get; set; } = string.Empty;

        [Required(ErrorMessage = "送货单号不能为空")]
        public string PackSlip { get; set; } = string.Empty;

        [Range(1, int.MaxValue, ErrorMessage = "采购订单行号必须为正整数")]
        public int POLine { get; set; }

        [Required(ErrorMessage = "物料编码不能为空")]
        public string PartNum { get; set; } = string.Empty;

        [Range(0.000001, double.MaxValue, ErrorMessage = "收货数量必须大于 0")]
        public decimal Quantity { get; set; }

        [Required(ErrorMessage = "单位不能为空")]
        public string Unit { get; set; } = string.Empty;

        [Required(ErrorMessage = "仓库不能为空")]
        public string WarehouseCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "库位不能为空")]
        public string Location { get; set; } = string.Empty;

        [Required(ErrorMessage = "批次号不能为空")]
        public string BatchNo { get; set; } = string.Empty;
    }

    #endregion

    #region 采购收货 - PO 明细信息

    /// <summary>
    /// 采购收货 - PO 明细信息项 DTO
    /// </summary>
    public class POInfoItem
    {
        public int POLine { get; set; }
        public string PartNum { get; set; } = string.Empty;
        public string PartDescription { get; set; } = string.Empty;
        public decimal OrderQty { get; set; }
        public decimal OurQty { get; set; }
        public string PONumApprovalStatus { get; set; } = string.Empty;
        public bool PONumConfirmed { get; set; }
        public string WareHouseCode { get; set; } = string.Empty;
        public string BinNum { get; set; } = string.Empty;
        public string LotNum { get; set; } = string.Empty;
        public string IUM { get; set; } = string.Empty;
        public string PUM { get; set; } = string.Empty;
        public string TranType { get; set; } = string.Empty;
    }

    /// <summary>
    /// 采购收货 - PO 信息结果 DTO
    /// </summary>
    public class POInfoResult
    {
        public string PONum { get; set; } = string.Empty;
        public int VendorNum { get; set; }
        public string VendorName { get; set; } = string.Empty;
        public string POType { get; set; } = string.Empty;
        public string POStatus { get; set; } = string.Empty;
        public decimal TotalAmt { get; set; }
        public string ShipViaCode { get; set; } = string.Empty;
        public string TaxRegionCode { get; set; } = string.Empty;
        public string CurrencyCode { get; set; } = string.Empty;
        public string PODueDate { get; set; } = string.Empty;
        public List<POInfoItem> Items { get; set; } = new();
        public JObject? RawDs { get; set; }
    }

    #endregion

    #region 采购收货 - PO 明细搜索

    /// <summary>
    /// PO明细搜索结果项（来自 PODetailLineSearchSvc/GetRows）
    /// </summary>
    public class POLineSearchItem
    {
        public string PONUM { get; set; } = string.Empty;
        public int POLine { get; set; }
        public string PartNum { get; set; } = string.Empty;
        public string PartDescription { get; set; } = string.Empty;
        public decimal OrderQty { get; set; }
        public decimal OurQty { get; set; }
        public decimal ReceivedQty { get; set; }
        public bool OpenLine { get; set; }
        public string WareHouseCode { get; set; } = string.Empty;
        public string BinNum { get; set; } = string.Empty;
        public string LotNum { get; set; } = string.Empty;
        public string IUM { get; set; } = string.Empty;
        public string PUM { get; set; } = string.Empty;
        public string VendorNum { get; set; } = string.Empty;
        public int VendorNumInt { get; set; }
        public string VendorName { get; set; } = string.Empty;
        public decimal RemainingQty => OrderQty - (OurQty > 0 ? OurQty : ReceivedQty);
    }

    #endregion

    #region 采购收货 - BAQ 查询结果

    /// <summary>
    /// OpenPODetail BAQ 查询结果
    /// </summary>
    public class POBAQResult
    {
        public string PoNo { get; set; } = string.Empty;
        public int PoLine { get; set; }
        public int PORelNum { get; set; }
        public string PartNum { get; set; } = string.Empty;
        public decimal RelQty { get; set; }
        public decimal ReceivedQty { get; set; }
        public int VendorNum { get; set; }
        public string VendorID { get; set; } = string.Empty;

        /// <summary>采购订单单位（IUM），用于与扫码单位比对；BAQ 缺列时为空</summary>
        public string IUM { get; set; } = string.Empty;

        public decimal AvailableQty => RelQty - ReceivedQty;

        // ============ PO / 行状态字段（仓库侧显式校验） ============

        /// <summary>PODetail.OpenLine：行级状态，true=未结可收货，false=已结不可收货</summary>
        public bool? OpenLine { get; set; }

        /// <summary>POHeader.ApprovalStatus：PO 头审核状态；A=已审核，U=未审核</summary>
        public string ApprovalStatus { get; set; } = string.Empty;
    }

    #endregion
}
