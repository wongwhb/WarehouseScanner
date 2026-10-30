using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    public class Factory
    {
        [Required(ErrorMessage = "工厂代码不能为空")]
        [Display(Name = "工厂代码")]
        public string Code { get; set; } = "";

        [Required(ErrorMessage = "工厂名称不能为空")]
        [Display(Name = "工厂名称")]
        public string Name { get; set; } = "";

        [Display(Name = "中文名称")]
        public string NameCN { get; set; } = "";

        // 修改点1: 添加Required特性
        [Required(ErrorMessage = "描述不能为空")]
        [Display(Name = "描述")]
        public string Description { get; set; } = "";

        [Display(Name = "中文描述")]
        public string DescriptionCN { get; set; } = "";

        [Display(Name = "位置")]
        public string Location { get; set; } = "";

        /// <summary>
        /// Epicor 公司代码。必填：多站点隔离以 Company 为维度，缺省会引发跨站点数据混用。
        /// </summary>
        [Required(ErrorMessage = "公司代码不能为空")]
        [Display(Name = "公司")]
        public string Company { get; set; } = "";

        /// <summary>
        /// Epicor 工厂(Plant)。必填：BAQ 的 @Plant 过滤、CallSettings 与锁/幂等键都依赖它，
        /// 缺省会让查询越过工厂边界（禁止硬编码默认值兜底）。
        /// </summary>
        [Required(ErrorMessage = "工厂(Plant)不能为空")]
        [Display(Name = "工厂(Plant)")]
        public string Plant { get; set; } = "";

        /// <summary>
        /// 生效公司。Company 为必填项，此回退仅作为校验前的防御性读取，不参与序列化。
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public string EffectiveCompany => string.IsNullOrWhiteSpace(Company) ? Code.Trim() : Company.Trim();

        // 修改点2: 添加Required特性
        [Required(ErrorMessage = "ApiUrl不能为空")]
        [Display(Name = "ApiUrl")]
        public string ApiUrl { get; set; } = "";

        // 编辑器不回显明文 ApiKey，页面提交的是掩码（含 ***）或留空（表示保留原值）。
        // 因此这里不能用 [Required]——否则"留空 = 保留原值"会在 ModelState 阶段被直接拒绝。
        // 新增站点时的非空校验由 FactoryController.Create 显式完成。
        [Display(Name = "ApiKey")]
        public string ApiKey { get; set; } = "";

        [Display(Name = "图标")]
        public string Icon { get; set; } = "fa-building";

        [Display(Name = "启用状态")]
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// 登录页等只读场景使用的工厂轻量视图：仅含站点代码/名称，不含 ApiKey 等敏感字段。
    /// </summary>
    public sealed record FactoryOption(string Code, string Name);

    public class FactoriesConfig
    {
        public List<Factory> Factories { get; set; } = new List<Factory>();
    }
}