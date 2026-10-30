namespace WarehouseScannerCore.Utilities
{
    #region Cookie 名称常量
    /// <summary>Cookie 名称常量，避免魔法字符串分散在代码中</summary>
    public static class CookieNames
    {
        public const string Language = "Language";
        public const string LastSelectedSite = "LastSelectedSite";
    }
    #endregion

    #region Session Key 常量
    /// <summary>Session Key 常量，统一管理所有会话数据键</summary>
    public static class SessionKeys
    {
        public const string UserId = "UserId";
        public const string Username = "Username";
        public const string UserFullName = "UserFullName";
        public const string UserRole = "UserRole";
        /// <summary>历史键：发布前存站点代码，仅作旧会话兜底读取</summary>
        public const string CompanyCode = "CompanyCode";
        /// <summary>站点代码（主用）</summary>
        public const string SiteCode = "SiteCode";
        /// <summary>Epicor 公司代码</summary>
        public const string EpicorCompany = "EpicorCompany";
        /// <summary>Epicor 工厂(Plant)，空串 = 不限定工厂</summary>
        public const string Plant = "Plant";
        public const string LoginTime = "LoginTime";
        public const string ApiUrl = "ApiUrl";
        public const string Password = "Password";
        public const string ApiKey = "ApiKey";
    }
    #endregion

    #region 权限/菜单常量
    /// <summary>权限/菜单常量，避免魔法字符串</summary>
    public static class MenuCodes
    {
        public const string Issue = "Issue";
        public const string ReturnMaterial = "ReturnMaterial";
        public const string Outbound = "Outbound";
        public const string Inbound = "Inbound";
        public const string Receivin = "Receivin";
        public const string Transfer = "Transfer";
        public const string Inventory = "Inventory";
        public const string LabelPrint = "LabelPrint";
        public const string Settings = "Settings";
    }
    #endregion

    #region 缓存键常量
    /// <summary>缓存键常量</summary>
    public static class CacheKeys
    {
        public const string UserMenuPermissions = "UserMenuPermissions";
        public const string LanguageResourcesPrefix = "LanguageResources_";
        public const string SupportedLanguages = "SupportedLanguages";
        public const string FactoryConfig = "FactoryConfig";
    }
    #endregion

    #region 通用常量
    /// <summary>通用常量</summary>
    public static class AppDefaults
    {
        public const string DefaultLanguage = "zh-CN";
        public const string FallbackLanguage = "en-US";
        public const int PermissionAllowed = 1;
        public const int PermissionDenied = 0;
    }
    #endregion
}
