using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WarehouseScanner.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 业务服务基类
    /// 职责：统一封装跨业务服务（发料 / 入库 / ...）的通用基础设施：
    /// 1. 工单级并发锁（带 TTL 自动清理）
    /// 2. 幂等性缓存（前置 + 双重检查）
    /// 3. TraceId 获取
    /// 4. 共享依赖（Logger / QueryGateway / BusinessServiceFactory / HttpContextAccessor / MemoryCache）
    /// 设计目标：消除 IssueBusinessService / ReceiptBusinessService 之间的重复代码，
    ///           同时保持各业务服务锁命名空间相互隔离（通过 LockKeyPrefix 区分）。
    /// 锁 / 幂等 / TraceId 全部委托给 <see cref="IBusinessLockProvider"/>（全局单例），
    ///           与 ReturnController / TransferController / OutboundController 共享同一锁字典。
    /// </summary>
    public abstract class BaseBusinessService
    {
        #region 共享依赖

        protected readonly ILogger Logger;
        protected readonly IEpicorQueryGateway QueryGateway;
        protected readonly IEpicorBusinessServiceFactory BusinessServiceFactory;
        protected readonly IHttpContextAccessor HttpContextAccessor;
        protected readonly IMemoryCache MemoryCache;
        /// <summary>每日单据操作明细日志：提交成功/失败时写入 logs/单据操作明细_yyyyMMdd.txt</summary>
        protected readonly DocumentOperationLogService DocumentOperationLogger;
        private readonly IBusinessLockProvider _lockProvider;

        protected BaseBusinessService(
            ILogger logger,
            IEpicorQueryGateway queryGateway,
            IEpicorBusinessServiceFactory businessServiceFactory,
            IHttpContextAccessor httpContextAccessor,
            IMemoryCache memoryCache,
            IBusinessLockProvider lockProvider,
            DocumentOperationLogService documentOperationLogger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            QueryGateway = queryGateway ?? throw new ArgumentNullException(nameof(queryGateway));
            BusinessServiceFactory = businessServiceFactory ?? throw new ArgumentNullException(nameof(businessServiceFactory));
            HttpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            MemoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
            DocumentOperationLogger = documentOperationLogger ?? throw new ArgumentNullException(nameof(documentOperationLogger));
        }

        #endregion

        #region 本地化

        /// <summary>
        /// 从 DI 解析 ILanguageService 获取本地化文本（运行时解析，无需改构造函数）。
        /// 语言来源：Cookie[CookieNames.Language]，缺省回退 AppDefaults.DefaultLanguage。
        /// 仅用于"返回给前端操作员"的消息；内部日志保持中文。
        /// 含动态内容时由调用方 string.Format(GetText(...), args)。
        /// </summary>
        protected string GetText(string category, string key)
        {
            try
            {
                var langService = HttpContextAccessor.HttpContext?.RequestServices.GetService(typeof(ILanguageService))
                    as ILanguageService;
                if (langService != null)
                {
                    var culture = HttpContextAccessor.HttpContext?.Request.Cookies[WarehouseScannerCore.Utilities.CookieNames.Language]
                        ?? WarehouseScannerCore.Utilities.AppDefaults.DefaultLanguage;
                    // 基于资源存在性判定，命中才返回；未命中输出可读兜底，避免界面出现占位符或空白。
                    // 走 TryGetRawString：这些文案最终进入 JSON API 响应（非 HTML 上下文），
                    // 若用加载期 HtmlEncode 后的值，操作员会看到 &amp; / &#39; 字面量。
                    if (langService.TryGetRawString(culture, category, key, out var text) &&
                        !string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                    if (langService.TryGetRawString(culture, "Common", "OperationFailedRetry", out var fallback) &&
                        !string.IsNullOrEmpty(fallback))
                    {
                        return fallback;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "获取本地化文本失败，使用通用兜底文案。Category:{Category} Key:{Key}", category, key);
            }
            return "操作失败，请稍后重试";
        }

        #endregion

        #region 锁 / 幂等常量

        /// <summary>
        /// 锁键前缀（由子类提供，确保不同业务服务的锁互不影响）。
        /// 例如：IssueBusinessService 返回 "Issue"，ReceiptBusinessService 返回 "Receipt"。
        /// </summary>
        protected abstract string LockKeyPrefix { get; }

        /// <summary>
        /// 幂等缓存键前缀（由子类提供）。
        /// 例如：IssueBusinessService 返回 "IssueRequest:"，ReceiptBusinessService 返回 "ReceiptRequest:"。
        /// </summary>
        protected abstract string RequestCacheKeyPrefix { get; }

        /// <summary>
        /// 带租户隔离的幂等键前缀。业务锁/幂等缓存是应用级单例，
        /// 键必须带租户，否则跨站点对同一 requestId 会互相误判。
        /// </summary>
        private string TenantScopedRequestCacheKey => $"{TenantKey}|{RequestCacheKeyPrefix}";

        #endregion

        #region 缓存清理 / TraceId

        /// <summary>
        /// 从当前 HttpContext 获取 TraceId（兜底回退到 Activity.Id 或新建 Guid）。
        /// </summary>
        protected string GetTraceId()
            => _lockProvider.GetTraceId(HttpContextAccessor.HttpContext);

        #endregion

        #region 幂等性

        /// <summary>
        /// 查询 requestId 的幂等结果码：null = 未处理；
        /// <see cref="IBusinessLockProvider.RequestResultSuccess"/> = 已成功处理；
        /// <see cref="IBusinessLockProvider.RequestResultUnknown"/> = 已提交但结果未知。
        /// 命中记录时**必须**按结果码区分应答，否则会把"上次失败"回报成"成功"。
        /// </summary>
        protected string? TryGetRequestResult(string? requestId)
            => _lockProvider.TryGetRequestResult(TenantScopedRequestCacheKey, requestId);

        /// <summary>
        /// 标记 requestId 已成功处理（写入幂等性缓存，TTL = 60 分钟）。
        /// </summary>
        protected void MarkRequestProcessed(string? requestId)
            => _lockProvider.MarkRequestProcessed(TenantScopedRequestCacheKey, requestId);

        /// <summary>
        /// 标记 requestId 已处理并记录结果码。
        /// 持久化操作已发出但结果未知（Epicor 报错 / 抛异常）时**必须**传
        /// <see cref="IBusinessLockProvider.RequestResultUnknown"/>。
        /// </summary>
        protected void MarkRequestProcessed(string? requestId, string result)
            => _lockProvider.MarkRequestProcessed(TenantScopedRequestCacheKey, requestId, result);

        #endregion

        #region 锁机制

        /// <summary>
        /// 租户键（站点|公司|工厂 小写），从当前 Session 派生。
        /// 业务锁为应用级单例，锁键必须带租户，否则跨站点对同一业务键互斥导致误阻塞。
        /// 公司/工厂由工厂配置与认证实例同源解析，旧会话不会派生出行不通的键。
        /// </summary>
        protected string TenantKey
        {
            get
            {
                var context = HttpContextAccessor.HttpContext
                    ?? throw new UnauthorizedAccessException("会话不可用，请重新登录");
                return EpicorSessionHelper.GetTenantKey(
                    HttpContextAccessor.GetRequiredSession(),
                    context.RequestServices.GetRequiredService<FactoryConfigService>());
            }
        }

        /// <summary>
        /// 尝试获取业务级并发锁（带 30 秒超时）。
        /// 返回 null 表示获取超时或锁异常；调用方应在 finally 中调用 <see cref="ReleaseLock"/> 释放。
        /// </summary>
        /// <param name="lockKey">业务级锁键（如 "工单号_装配序号"），方法内部会自动加 LockKeyPrefix + 租户前缀。</param>
        /// <param name="traceId">用于日志关联的 TraceId。</param>
        /// <param name="operatorId">操作员标识（仅用于超时日志）。</param>
        protected Task<SemaphoreSlim?> TryAcquireLockAsync(
            string lockKey, string traceId, string operatorId)
            => _lockProvider.TryAcquireLockAsync(LockKeyPrefix, TenantKey + "|" + lockKey, traceId, operatorId);

        /// <summary>
        /// 释放锁。该方法在 5 个业务服务的 finally 中调用，因此**释放路径绝不允许抛异常**：
        /// finally 中抛出的异常会覆盖业务返回值，且会让信号量永久不被归还
        /// （该业务键之后的所有请求都只能等到超时后失败，需重启进程才能恢复）。
        /// 容错：SemaphoreFullException 由 provider 内部处理；其余异常（典型是会话已失效，
        /// 导致 <see cref="TenantKey"/> 派生失败）在此兜底，直接归还信号量句柄。
        /// 锁键拼接规则必须与 TryAcquireLockAsync 完全一致。
        /// </summary>
        protected void ReleaseLock(SemaphoreSlim? lockHandle, string lockKey, string traceId)
        {
            if (lockHandle == null) return;

            try
            {
                _lockProvider.ReleaseLock(lockHandle, LockKeyPrefix, TenantKey + "|" + lockKey, traceId);
            }
            catch (Exception ex)
            {
                // 兜底：即使拿不到租户键，也必须归还信号量，避免业务键被永久锁死。
                // 代价是该键的持有计数无法递减（清理器不会回收此条目），但业务可用性优先。
                Logger?.LogError(ex,
                    "[{TraceId}] 释放业务锁失败（租户键不可用），已兜底归还信号量: LockKey={LockKey}", traceId, lockKey);
                try
                {
                    lockHandle.Release();
                }
                catch (Exception releaseEx)
                {
                    Logger?.LogWarning(releaseEx,
                        "[{TraceId}] 兜底归还信号量失败（可能已被重复释放）: LockKey={LockKey}", traceId, lockKey);
                }
            }
        }

        /// <summary>
        /// 从异常链中提取可直接展示给操作员的 Epicor 业务错误文案。
        /// 仅放行具备 Epicor 业务错误特征的文本（"[数字] ..." 或含 CorrelationId），
        /// 其余异常（网络 / 认证 / 反序列化等）一律返回 null，
        /// 避免把主机名、URL 等内部信息经由前端提示泄漏出去。
        /// </summary>
        public static string? ExtractEpicorBusinessError(Exception? ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                string message = current.Message;
                if (string.IsNullOrWhiteSpace(message)) continue;

                string trimmed = message.Trim();

                int closingBracket = trimmed.IndexOf(']');
                if (trimmed.StartsWith("[") && closingBracket > 1 && char.IsDigit(trimmed[1]))
                    return trimmed;

                if (trimmed.Contains("CorrelationId", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
            }
            return null;
        }

        #endregion
    }
}
