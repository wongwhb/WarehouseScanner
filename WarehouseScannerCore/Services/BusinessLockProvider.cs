using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// <see cref="IBusinessLockProvider"/> 的默认实现。
    /// 注册为 Singleton 生命周期：实例级并发锁字典 + 清理定时器在全局范围内共享，
    /// 所有业务（Issue / Receipt / Return / Transfer / Outbound）通过 lockKeyPrefix 在键空间相互隔离，避免相互阻塞。
    /// </summary>
    public class BusinessLockProvider : IBusinessLockProvider, IDisposable
    {
        #region 常量

        private const int LockWaitTimeoutSeconds = 30;
        // 幂等缓存 TTL：保持原 Controller 实现的 1 小时窗口，避免重复提交
        private const int RequestCacheTtlMinutes = 60;
        private const int LockTtlMinutes = 30;
        private static readonly TimeSpan LockCleanupInterval = TimeSpan.FromMinutes(10);

        #endregion

        #region 共享状态

        /// <summary>
        /// 锁条目：信号量 + 持有计数 + 最后活动时间。
        /// 持有计数用于清理器安全回收：仅当计数为 0（无人持有、无人等待）且超期时才移除，
        /// 杜绝"移除后被并发重建出第二个信号量"导致的互斥失效。
        /// </summary>
        private sealed class LockEntry
        {
            public readonly SemaphoreSlim Semaphore = new(1, 1);
            public int ActiveHolders;
            public DateTime LastActivityUtc = DateTime.UtcNow;
        }

        // 实例级并发锁字典
        // 注：BusinessLockProvider 注册为 Singleton，实例级字典等价于全局共享；
        //     改为实例字段便于 Dispose 时统一清理、提升单元测试隔离性。
        private readonly ConcurrentDictionary<string, LockEntry> _locks = new();

        // 清理定时器（实例字段，随 Singleton 生命周期释放）
        private readonly Timer _lockCleanupTimer;

        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<BusinessLockProvider> _logger;

        #endregion

        public BusinessLockProvider(IMemoryCache memoryCache, ILogger<BusinessLockProvider> logger)
        {
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _lockCleanupTimer = new Timer(
                _ => CleanupExpiredLocks(), null,
                LockCleanupInterval, LockCleanupInterval);
        }

        #region 清理

        private void CleanupExpiredLocks()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-LockTtlMinutes);
            foreach (var kv in _locks)
            {
                var entry = kv.Value;
                lock (entry)
                {
                    // 仅回收"无人持有/等待"且超期的条目。
                    // 获取侧在同一 entry 锁内注册持有计数（见 TryAcquireLockAsync），
                    // 配合 TryRemove(KeyValuePair) 的原子比较，保证一个键全程只对应一个信号量。
                    if (entry.ActiveHolders == 0 && entry.LastActivityUtc < cutoff)
                        _locks.TryRemove(new KeyValuePair<string, LockEntry>(kv.Key, entry));
                }
                // 不调用被移除信号量的 Dispose()：SemaphoreSlim 未访问 AvailableWaitHandle，
                // 不占非托管资源，交由 GC 回收即可。
            }
        }

        #endregion

        #region 锁机制

        public async Task<SemaphoreSlim?> TryAcquireLockAsync(
            string lockKeyPrefix, string lockKey, string traceId, string operatorId)
        {
            string fullKey = $"{lockKeyPrefix}:{lockKey}";

            // 注册持有计数与字典归属校验必须在同一 entry 锁内完成：
            // 清理器也在同一锁内判定可回收，二者互斥，不存在"计数已注册但条目被回收"的窗口。
            LockEntry entry;
            while (true)
            {
                entry = _locks.GetOrAdd(fullKey, _ => new LockEntry());
                lock (entry)
                {
                    if (_locks.TryGetValue(fullKey, out var current) && ReferenceEquals(current, entry))
                    {
                        entry.ActiveHolders++;
                        entry.LastActivityUtc = DateTime.UtcNow;
                        break;
                    }
                    // entry 刚被清理器回收，重试获取新条目
                }
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(LockWaitTimeoutSeconds));
            try
            {
                await entry.Semaphore.WaitAsync(cts.Token);
                return entry.Semaphore;
            }
            catch (OperationCanceledException)
            {
                // 超时未取到锁：归还持有计数
                lock (entry) { entry.ActiveHolders--; entry.LastActivityUtc = DateTime.UtcNow; }
                _logger.LogError("[{TraceId}] 获取业务锁超时: LockKey={LockKey}, Operator={Operator}",
                    traceId, fullKey, operatorId);
                return null;
            }
        }

        public void ReleaseLock(SemaphoreSlim? lockHandle, string lockKeyPrefix, string lockKey, string traceId)
        {
            if (lockHandle == null) return;
            string fullKey = $"{lockKeyPrefix}:{lockKey}";
            try
            {
                lockHandle.Release();
            }
            catch (SemaphoreFullException)
            {
                // 重复释放：计数未归还属正常（本次释放无效），仅记录，不影响主流程
                _logger.LogWarning("[{TraceId}] 锁重复释放: LockKey={LockKey}", traceId, fullKey);
                return;
            }

            // 释放成功：归还持有计数。仅当字典条目仍对应该信号量时更新，
            // 防止条目被回收重建后误减新条目计数（正常路径下持有中条目计数 >= 1，不会被回收）。
            if (_locks.TryGetValue(fullKey, out var entry) && ReferenceEquals(entry.Semaphore, lockHandle))
            {
                lock (entry) { entry.ActiveHolders--; entry.LastActivityUtc = DateTime.UtcNow; }
            }
        }

        #endregion

        #region 幂等性

        public string? TryGetRequestResult(string cacheKeyPrefix, string? requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return null;
            return _memoryCache.TryGetValue($"{cacheKeyPrefix}{requestId}", out var result)
                ? result as string
                : null;
        }

        public void MarkRequestProcessed(string cacheKeyPrefix, string? requestId, string result)
        {
            if (!string.IsNullOrEmpty(requestId))
                _memoryCache.Set($"{cacheKeyPrefix}{requestId}", result, TimeSpan.FromMinutes(RequestCacheTtlMinutes));
        }

        #endregion

        #region TraceId

        public string GetTraceId(HttpContext? httpContext)
            => httpContext?.TraceIdentifier
            ?? System.Diagnostics.Activity.Current?.Id
            ?? Guid.NewGuid().ToString("N");

        #endregion

        public void Dispose()
        {
            _lockCleanupTimer?.Dispose();
            // 清空实例级锁字典，便于单元测试多次构建实例时的状态隔离；
            // 不在此处主动 Dispose SemaphoreSlim——可能与正在运行的 Timer 回调
            // （CleanupExpiredLocks 正在遍历该字典）产生竞态触发 ObjectDisposedException。
            // SemaphoreSlim 的底层资源（如已创建）由 GC 终结器兜底释放。
            _locks.Clear();
        }
    }
}
