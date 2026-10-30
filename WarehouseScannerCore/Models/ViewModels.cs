using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    // 登录视图模型
    public class LoginViewModel
    {
        [Required(ErrorMessage = "用户名不能为空")]
        [Display(Name = "用户名")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "密码不能为空")]
        [DataType(DataType.Password)]
        [Display(Name = "密码")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "记住我")]
        public bool RememberMe { get; set; }

        public string? ReturnUrl { get; set; }
    }

    // 扫码视图模型
    public class ScanViewModel
    {
        public int OrderId { get; set; }
        public string? OrderNumber { get; set; }
        public string? Barcode { get; set; }
        public int Quantity { get; set; } = 1;
        public string? ShelfNumber { get; set; }
        public List<ScanRecord>? RecentScans { get; set; }
        public Product? CurrentProduct { get; set; }
    }

    // 订单创建视图模型
    public class CreateOrderViewModel
    {
        [Required(ErrorMessage = "供应商不能为空")]
        [Display(Name = "供应商")]
        public string Supplier { get; set; } = string.Empty;

        [Required(ErrorMessage = "收货人不能为空")]
        [Display(Name = "收货人")]
        public string Receiver { get; set; } = string.Empty;

        [Display(Name = "备注")]
        public string? Notes { get; set; }
    }

    // 首页视图模型
    public class HomeViewModel
    {
     
        public string UserName { get; set; } = string.Empty;
        public string UserRole { get; set; } = string.Empty;
        public DashboardStatistics Statistics { get; set; } = new DashboardStatistics();
    }

    public class DashboardStatistics
    {
        public int TotalOrders { get; set; }
        public int TotalProducts { get; set; }
        public int TodayScans { get; set; }
        public int PendingOrders { get; set; }
    }
}