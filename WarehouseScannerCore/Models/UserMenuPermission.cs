// Models/UserMenuPermission.cs
namespace WarehouseScannerCore.Models
{
    public class UserMenuPermission
    {
        /// <summary>站点代码。空串 = 全部站点（遗留全局记录的兼容语义，避免存量权限失效）。</summary>
        public string SiteCode { get; set; } = string.Empty;
        public string UserCode { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string UserGroup { get; set; } = string.Empty;
        public string MenuCode { get; set; } = string.Empty;
        public string MenuName { get; set; } = string.Empty;
        public int Permission { get; set; } = 1; // 1=允许, 0=禁止
        public string Remark { get; set; } = string.Empty;
    }

    public class UserMenuPermissionData
    {
        public List<UserMenuPermission> Permissions { get; set; } = new List<UserMenuPermission>();
    }
}