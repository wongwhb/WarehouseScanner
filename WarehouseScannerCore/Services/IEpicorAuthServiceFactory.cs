// IEpicorAuthServiceFactory.cs
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WarehouseScannerCore.Configuration;
using WarehouseScannerCore.Utilities;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using FactoryConfigService = WarehouseScannerCore.Services.FactoryConfigService;

namespace WarehouseScanner.Services
{
    public interface IEpicorAuthServiceFactory
    {
        /// <summary>
        /// 从 Session 创建或获取服务实例
        /// </summary>
        EpicorAuthService CreateFromSession(ISession session, IDataProtector protector);

        /// <summary>
        /// 根据凭证创建或获取服务实例（自动清理过期缓存）
        /// </summary>
        EpicorAuthService Create(string userId, string password, string apiUrl, string apiKey, EpicorSiteTarget target);

        /// <summary>
        /// 创建登录校验专用的服务实例（不入缓存）。
        /// 用于 AccountController 登录时校验 Epicor 凭据与获取员工信息。
        /// 调用方在校验完成后必须调用 LogoutAsync + Dispose 释放该校验会话，避免残留 License。
        /// </summary>
        EpicorAuthService CreateLoginSession(string userId, string password, string apiUrl, string apiKey, EpicorSiteTarget target);

        /// <summary>
        /// 移除指定用户/站点的服务实例，并调用 Epicor Logout 释放服务端 Session/License
        /// </summary>
        Task RemoveAsync(string userId, string apiUrl, EpicorSiteTarget target);
        
        /// <summary>
        /// 清除所有服务实例，并调用 Epicor Logout 释放所有服务端 Session/License
        /// </summary>
        Task ClearAsync();

        /// <summary>
        /// 清理所有过期的缓存实例（超过 CacheMaxAgeMinutes 未访问），返回清理数量。
        /// 由后台定时任务周期调用，避免过期实例长期驻留内存。
        /// </summary>
        int CleanupExpired();
    }

    /// <summary>
    /// EpicorAuthService 工厂
    /// License 最小化策略：
    /// - 支持 grant_type=refresh_token 刷新（不创建新 Session，不额外占用 License）
    /// - password 重登前先 Logout 释放旧 Session（避免 License 叠加）
    /// - LRU 缓存过期：超过 CacheMaxAgeMinutes 未访问自动清理，防止内存泄漏
    /// </summary>
    public class EpicorAuthServiceFactory : IEpicorAuthServiceFactory
    {
        private readonly ConcurrentDictionary<string, EpicorAuthService> _serviceCache = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastAccessTime = new();
        private readonly ILogger<EpicorAuthService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptions<EpicorSettings> _epicorSettings;
        private readonly FactoryConfigService _factoryService;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public EpicorAuthServiceFactory(
            ILogger<EpicorAuthService> logger,
            IHttpClientFactory httpClientFactory,
            IOptions<EpicorSettings> epicorSettings,
            FactoryConfigService factoryService,
            IHttpContextAccessor httpContextAccessor)
        {
            _logger = logger ?? NullLogger<EpicorAuthService>.Instance;
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _epicorSettings = epicorSettings ?? throw new ArgumentNullException(nameof(epicorSettings));
            _factoryService = factoryService ?? throw new ArgumentNullException(nameof(factoryService));
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        }

        private string GetCacheKey(string userId, string apiUrl, EpicorSiteTarget target)
        {
            // 键含 SiteCode 段：两站点 apiUrl/company/plant 相同也不共享认证实例。
            // 创建与移除共用本方法，两键天然一致。
            return $"{userId}|{apiUrl}|{target.SiteCode}|{target.Company}|{target.Plant}".ToLowerInvariant();
        }

        /// <summary>
        /// 安全解密 Session 中受保护的字段。
        /// 解密失败（密钥轮换 / Cookie 被篡改 / 跨环境部署）时不抛异常、不崩溃，
        /// 记录日志后返回空串，由后续的必填校验给出友好中文错误。
        /// </summary>
        private string SafeUnprotect(IDataProtector protector, string? encryptedValue, string fieldName)
        {
            if (string.IsNullOrEmpty(encryptedValue)) return string.Empty;
            try
            {
                return protector.Unprotect(encryptedValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解密 Session 字段失败（可能密钥轮换或会话数据被篡改）: {Field}", fieldName);
                return string.Empty;
            }
        }

        public EpicorAuthService CreateFromSession(ISession session, IDataProtector protector)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (protector == null) throw new ArgumentNullException(nameof(protector));

            var apiUrl = session.GetString(SessionKeys.ApiUrl) ?? string.Empty;
            var encryptedApiKey = session.GetString(SessionKeys.ApiKey);
            var apiKey = SafeUnprotect(protector, encryptedApiKey, SessionKeys.ApiKey);

            // 站点三元组：新会话直接读取；旧会话（发布前登录，仅有历史 CompanyCode 键）
            // 由 FactoryConfigService.ResolveTarget 补全（与登出清理、租户键派生同源），避免把站点代码误当公司。
            var target = _factoryService.ResolveTarget(
                EpicorSessionHelper.GetSiteCode(session),
                EpicorSessionHelper.GetEpicorCompany(session),
                EpicorSessionHelper.GetPlant(session));

            // 使用 Session 中的用户凭证
            var username = session.GetString(SessionKeys.UserId) ?? string.Empty;
            var encryptedPassword = session.GetString(SessionKeys.Password);
            var password = SafeUnprotect(protector, encryptedPassword, SessionKeys.Password);

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) ||
                string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException("Session 中缺少必要的 Epicor 连接信息");
            }

            return Create(username, password, apiUrl, apiKey, target);
        }

        public EpicorAuthService Create(string userId, string password, string apiUrl, string apiKey, EpicorSiteTarget target)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));
            if (string.IsNullOrWhiteSpace(apiUrl))
                throw new ArgumentNullException(nameof(apiUrl));
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentNullException(nameof(apiKey));
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            var cacheKey = GetCacheKey(userId, apiUrl, target);

            var maxAge = TimeSpan.FromMinutes(_epicorSettings.Value.CacheMaxAgeMinutes);

            // 检查过期实例 — 超过 CacheMaxAgeMinutes 未访问则清理
            if (_serviceCache.TryGetValue(cacheKey, out var existing))
            {
                bool needRemove = false;

                if (_lastAccessTime.TryGetValue(cacheKey, out var lastAccess) &&
                    DateTime.UtcNow - lastAccess > maxAge)
                {
                    _logger.LogInformation(
                        "缓存实例已过期（超过 {MaxAge} 分钟未访问），移除并重建 - Key: {CacheKey}",
                        _epicorSettings.Value.CacheMaxAgeMinutes, cacheKey);
                    needRemove = true;
                }
                else if (!existing.HasCredentials(userId, password, apiKey))
                {
                    _logger.LogInformation(
                        "检测到凭证变更（Key: {CacheKey}），移除旧实例并重建", cacheKey);
                    needRemove = true;
                }

                if (needRemove)
                {
                    // 同步路径（Create 非 async）：缓存键已原子移除，后台异步执行 Logout 释放 License，
                    // 不阻塞当前请求线程；同一实例只会被移除一次，因此不会重复登出。
                    if (_serviceCache.TryRemove(cacheKey, out var removed))
                    {
                        ScheduleLogout(removed, cacheKey);
                    }
                    _lastAccessTime.TryRemove(cacheKey, out _);
                }
            }

            var service = _serviceCache.GetOrAdd(cacheKey, key =>
            {
                _logger.LogInformation("创建新的 EpicorAuthService 实例 - 用户: {UserId}, 公司: {Company}, 工厂: {Plant}",
                    userId, target.Company, target.Plant);
                return new EpicorAuthService(
                    userId, password, apiUrl, apiKey,
                    _httpClientFactory, _epicorSettings,
                    target.Company, target.Plant, _logger, _httpContextAccessor);
            });

            // 更新访问时间
            _lastAccessTime[cacheKey] = DateTime.UtcNow;

            return service;
        }

        /// <summary>
        /// 在后台异步执行一次 Logout 并释放实例（用于同步缓存淘汰路径）：
        /// - 不阻塞调用线程（不使用 .GetAwaiter().GetResult()）
        /// - 异常只写日志，不向上冒泡
        /// - 调用前必须已用 TryRemove 原子摘除缓存键，保证同一实例只调度一次、不会重复登出
        /// </summary>
        private void ScheduleLogout(EpicorAuthService? service, string cacheKey)
        {
            if (service == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await service.LogoutAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "缓存淘汰时 Epicor Logout 失败（非致命），Session 将依赖服务端超时回收 - Key: {CacheKey}", cacheKey);
                }
                finally
                {
                    service.Dispose();
                }
            });
        }

        /// <summary>
        /// 创建登录校验专用的服务实例（不入缓存）。
        /// 用于 AccountController 登录时校验 Epicor 凭据与获取员工信息。
        /// 调用方在校验完成后必须调用 LogoutAsync + Dispose 释放该校验会话，避免残留 License。
        /// </summary>
        public EpicorAuthService CreateLoginSession(string userId, string password, string apiUrl, string apiKey, EpicorSiteTarget target)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));
            if (string.IsNullOrWhiteSpace(apiUrl))
                throw new ArgumentNullException(nameof(apiUrl));
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentNullException(nameof(apiKey));
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            _logger.LogInformation(
                "创建登录校验专用 EpicorAuthService（独立会话，不入缓存） - 用户: {UserId}, 公司: {Company}, 工厂: {Plant}",
                userId, target.Company, target.Plant);

            return new EpicorAuthService(
                userId, password, apiUrl, apiKey,
                _httpClientFactory, _epicorSettings,
                target.Company, target.Plant, _logger, _httpContextAccessor);
        }

        /// <summary>
        /// 移除指定缓存实例，并调用 Epicor Logout 释放服务端 Session/License
        /// </summary>
        public async Task RemoveAsync(string userId, string apiUrl, EpicorSiteTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            // 旧会话兜底：发布前登录的会话无 EpicorCompany 键，统一走 ResolveTarget（与实例创建同源），
            // 否则此处拼出的缓存键与实例创建键不一致，登出会 miss（License 只能等服务端超时回收）
            target = _factoryService.ResolveTarget(target.SiteCode, target.Company, target.Plant);

            var cacheKey = GetCacheKey(userId, apiUrl, target);

            if (_serviceCache.TryRemove(cacheKey, out var service))
            {
                _logger.LogInformation("移除 EpicorAuthService 实例 - 用户: {UserId}, 公司: {Company}, 工厂: {Plant}", userId, target.Company, target.Plant);
                try
                {
                    // 显式调用 Epicor Logout 释放服务端 Session/License
                    await service.LogoutAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Epicor Logout 调用失败（非致命） - 用户: {UserId}", userId);
                }
                finally
                {
                    service?.Dispose();
                }
            }
            _lastAccessTime.TryRemove(cacheKey, out _);
        }

        /// <summary>
        /// 清除所有缓存实例，并调用 Epicor Logout 释放所有服务端 Session/License
        /// </summary>
        public async Task ClearAsync()
        {
            _logger.LogInformation("清除所有 EpicorAuthService 实例，共 {Count} 个", _serviceCache.Count);

            foreach (var kvp in _serviceCache)
            {
                try
                {
                    await kvp.Value.LogoutAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Epicor Logout 调用失败（非致命）");
                }
                finally
                {
                    kvp.Value?.Dispose();
                }
            }

            _serviceCache.Clear();
            _lastAccessTime.Clear();
        }

        /// <summary>
        /// 清理所有过期的缓存实例（超过 CacheMaxAgeMinutes 未访问）
        /// 可由定时任务或应用空闲时调用
        /// </summary>
        public int CleanupExpired()
        {
            var maxAge = TimeSpan.FromMinutes(_epicorSettings.Value.CacheMaxAgeMinutes);
            int removed = 0;

            foreach (var kvp in _lastAccessTime)
            {
                if (DateTime.UtcNow - kvp.Value > maxAge)
                {
                    if (_serviceCache.TryRemove(kvp.Key, out var service))
                    {
                        // 同步路径（CleanupExpired 非 async）：缓存键已原子移除，后台异步执行 Logout 释放 License，
                        // 不阻塞调用线程；异常在后台记录，不向上冒泡；同一实例只会被移除一次。
                        ScheduleLogout(service, kvp.Key);
                        removed++;
                    }
                    _lastAccessTime.TryRemove(kvp.Key, out _);
                }
            }

            if (removed > 0)
                _logger.LogInformation("清理过期缓存实例 {Count} 个", removed);

            return removed;
        }
    }
}
