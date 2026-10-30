using System;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models.DTOs
{
    #region 工单验证请求
    /// <summary>
    /// 工单状态验证请求 DTO
    /// </summary>
    public class ValidateJobRequest
    {
        [Required(ErrorMessage = "工单号不能为空")]
        public string WorkOrderNo { get; set; } = string.Empty;

        [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
        public int AssemblySeq { get; set; }
    }
    #endregion

    #region 库存验证请求
    /// <summary>
    /// 物料库存验证请求 DTO
    /// </summary>
    public class ValidateInventoryRequest
    {
        [Required(ErrorMessage = "工单号不能为空")]
        public string WorkOrderNo { get; set; } = string.Empty;

        [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
        public int AssemblySeq { get; set; }

        [Required(ErrorMessage = "物料代码不能为空")]
        public string MaterialCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "单位不能为空")]
        public string Unit { get; set; } = string.Empty;

        // 仓库必须显式指定：仓库为空会使库存校验退化为全厂汇总（BAQ 不带 WarehouseCode 过滤），
        // 导致按本仓库存拦截超发的校验失效。保留可空声明以兼容既有赋值路径。
        [Required(ErrorMessage = "仓库不能为空")]
        public string? FromWarehouse { get; set; }

        public string? Location { get; set; }

        [Range(0.0001, double.MaxValue, ErrorMessage = "数量必须 > 0")]
        public decimal Quantity { get; set; }

        public string? BatchNo { get; set; }
    }
    #endregion

    #region 发料请求
    /// <summary>
    /// 物料发料请求 DTO
    /// 设计说明：
    /// - 所有关键字段使用 Required 注解，保证 ModelState 校验
    /// - Quantity 使用 Range 注解防 0/负数
    /// - 可为空字段明确标记为 nullable
    /// </summary>
    public class IssueMaterialRequest
    {
        [Required(ErrorMessage = "工单号不能为空")]
        public string WorkOrderNo { get; set; } = string.Empty;

        [Range(0, int.MaxValue, ErrorMessage = "装配序号必须 >= 0")]
        public int AssemblySeq { get; set; }

        [Required(ErrorMessage = "物料代码不能为空")]
        public string MaterialCode { get; set; } = string.Empty;

        [Range(0.0001, double.MaxValue, ErrorMessage = "数量必须 > 0")]
        public decimal Quantity { get; set; }

        [Required(ErrorMessage = "单位不能为空")]
        public string Unit { get; set; } = string.Empty;

        // 仓库必须显式指定：仓库为空会使库存校验退化为全厂汇总（BAQ 不带 WarehouseCode 过滤），
        // 导致按本仓库存拦截超发的校验失效。保留可空声明以兼容既有赋值路径。
        [Required(ErrorMessage = "仓库不能为空")]
        public string? FromWarehouse { get; set; }

        public string? Location { get; set; }

        public string? BatchNo { get; set; }
    }
    #endregion

    #region 发料结果响应
    /// <summary>
    /// 发料结果响应 DTO
    /// 用于发料完成后向前端返回操作结果
    /// </summary>
    public class IssueMaterialResult
    {
        public string WorkOrderNo { get; set; } = string.Empty;
        public int AssemblySeq { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string? BatchNo { get; set; }
        public string? Location { get; set; }
        public string Operator { get; set; } = string.Empty;
        public string IssueTime { get; set; } = string.Empty;
    }
    #endregion

}
