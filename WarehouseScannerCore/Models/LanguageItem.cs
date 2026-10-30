using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    public class LanguageItem
    {
        [Required(ErrorMessage = "请选择页面")]
        [Display(Name = "页面")]
        public string Page { get; set; } = null!;

        [Required(ErrorMessage = "请输入键名")]
        [Display(Name = "键名")]
        public string Key { get; set; } = null!;

        [Required(ErrorMessage = "请输入中文名称")]
        [Display(Name = "中文名称")]
        public string ChineseValue { get; set; } = null!;

        [Required(ErrorMessage = "请输入英文名称")]
        [Display(Name = "英文名称")]
        public string EnglishValue { get; set; } = null!;
    }
}
