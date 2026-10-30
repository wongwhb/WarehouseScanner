using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WarehouseScanner.Services
{
    /// <summary>
    /// 后台定时清理过期的 EpicorAuthService 缓存实例（空闲实例主动驱逐）。
    /// 若长期无人访问，缓存实例会驻留内存直到底层 Epicor 服务端超时回收；
    /// 此任务周期性调用 CleanupExpired 主动驱逐本地过期条目。
    /// </summary>
    public class EpicorSessionCleanupService : BackgroundService
    {
        private readonly IEpicorAuthServiceFactory _factory;
        private readonly ILogger<EpicorSessionCleanupService> _logger;
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

        public EpicorSessionCleanupService(
            IEpicorAuthServiceFactory factory,
            ILogger<EpicorSessionCleanupService> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(CleanupInterval, stoppingToken);
                    int removed = _factory.CleanupExpired();
                    if (removed > 0)
                        _logger.LogInformation("已清理过期 Epicor 会话实例 {Count} 个", removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理过期 Epicor 会话实例失败（非致命）");
                }
            }
        }
    }
}