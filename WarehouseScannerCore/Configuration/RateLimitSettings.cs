namespace WarehouseScannerCore.Configuration
{
    /// <summary>
    /// 速率限制配置
    /// 用于防止 API 滥用和 DDoS 攻击
    /// </summary>
    public class RateLimitSettings
    {
        /// <summary>
        /// 是否启用速率限制（默认启用）
        /// </summary>
        public bool EnableRateLimiting { get; set; } = true;

        /// <summary>
        /// 登录接口限制：每分钟最大请求数（默认 5 次/分钟，防暴力破解）
        /// </summary>
        public int LoginRequestsPerMinute { get; set; } = 5;

        /// <summary>
        /// 业务 API 限制：每分钟最大请求数（默认 60 次/分钟）
        /// </summary>
        public int ApiRequestsPerMinute { get; set; } = 60;

        /// <summary>
        /// 查询接口限制：每分钟最大请求数（默认 120 次/分钟）
        /// </summary>
        public int QueryRequestsPerMinute { get; set; } = 120;

        /// <summary>
        /// IP 白名单（不受速率限制的 IP，用于内部服务调用）
        /// </summary>
        public string[] IpWhitelist { get; set; } = new string[0];
    }
}
