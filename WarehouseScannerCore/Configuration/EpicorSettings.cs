namespace WarehouseScannerCore.Configuration
{
    #region Epicor 服务配置
    /// <summary>Epicor 服务配置（来自 appsettings.json "Epicor" 节点）</summary>
    public class EpicorSettings
    {
        /// <summary>许可证 ID</summary>
        public string LicenseId { get; set; } = "00000003-B8EB-40C4-8897-248CB30C5D47";

        /// <summary>Token 有效期（分钟），默认 480 分钟</summary>
        public int TokenExpiryMinutes { get; set; } = 480;

        /// <summary>提前刷新时间（分钟），默认 5 分钟</summary>
        public int TokenRefreshAheadMinutes { get; set; } = 5;

        /// <summary>HTTP 请求超时（秒），默认 30 秒</summary>
        public int DefaultTimeoutSeconds { get; set; } = 30;

        /// <summary>API 调用最大重试次数</summary>
        public int MaxRetries { get; set; } = 1;

        /// <summary>缓存实例最大存活时间（分钟），超时未访问自动清理。默认 120 分钟</summary>
        public int CacheMaxAgeMinutes { get; set; } = 120;

    }
    #endregion
}
