using Microsoft.AspNetCore.Http;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 业务级并发锁 + 幂等性缓存统一提供者。
    /// 职责：
    /// 1. 工单 / 转移 / 发货等业务级并发锁（带 TTL 自动清理 + 键前缀隔离）
    /// 2. 请求幂等性缓存（前置 + 双重检查）
    /// 3. TraceId 获取
    /// 设计目标：消除 Controller / BaseBusinessService 之间重复的锁字典与清理定时器代码，
    ///           通过 lockKeyPrefix 在键空间上隔离不同业务（Issue / Receipt / Return / Transfer / Outbound）。
    /// </summary>
    public interface IBusinessLockProvider
    {
        /// <summary>
        /// 尝试获取业务级并发锁（带 30 秒超时）。
        /// 返回 null 表示获取超时或锁异常；调用方应在 finally 中调用 <see cref="ReleaseLock"/> 释放。
        /// </summary>
        /// <param name="lockKeyPrefix">业务锁键前缀（如 "Return" / "Transfer" / "Outbound"），用于键空间隔离。</param>
        /// <param name="lockKey">业务级锁键（如 "工单号_装配序号"）。</param>
        /// <param name="traceId">用于日志关联的 TraceId。</param>
        /// <param name="operatorId">操作员标识（仅用于超时日志）。</param>
        Task<SemaphoreSlim?> TryAcquireLockAsync(
            string lockKeyPrefix, string lockKey, string traceId, string operatorId);

        /// <summary>
        /// 释放锁（容错处理 SemaphoreFullException / ObjectDisposedException）。
        /// </summary>
        void ReleaseLock(SemaphoreSlim? lockHandle, string lockKeyPrefix, string lockKey, string traceId);

        /// <summary>幂等结果码：请求已成功处理（Epicor 已确认落库）。</summary>
        const string RequestResultSuccess = "Success";

        /// <summary>
        /// 幂等结果码：请求已提交，但结果未知。
        /// 场景：持久化操作已发出，但 Epicor 返回报错或抛异常，无法确认是否已落库。
        /// 此时必须拦截重放，但**不得**向操作员回报成功。
        /// </summary>
        const string RequestResultUnknown = "Unknown";

        /// <summary>
        /// 查询 requestId 的幂等结果码：未命中返回 null；
        /// 命中返回 <see cref="RequestResultSuccess"/> 或 <see cref="RequestResultUnknown"/>。
        /// </summary>
        string? TryGetRequestResult(string cacheKeyPrefix, string? requestId);

        /// <summary>
        /// 标记 requestId 已处理并记录结果码（写入幂等性缓存，TTL = 60 分钟）。
        /// </summary>
        void MarkRequestProcessed(string cacheKeyPrefix, string? requestId, string result);

        /// <summary>
        /// 是否已有该 requestId 的处理记录（不区分成功 / 结果未知），仅用于拦截重放。
        /// </summary>
        bool IsRequestProcessed(string cacheKeyPrefix, string? requestId)
            => TryGetRequestResult(cacheKeyPrefix, requestId) != null;

        /// <summary>
        /// 标记 requestId 已成功处理。
        /// </summary>
        void MarkRequestProcessed(string cacheKeyPrefix, string? requestId)
            => MarkRequestProcessed(cacheKeyPrefix, requestId, RequestResultSuccess);

        /// <summary>
        /// 从当前 HttpContext 获取 TraceId（兜底回退到 Activity.Id 或新建 Guid）。
        /// </summary>
        string GetTraceId(HttpContext? httpContext);
    }
}
