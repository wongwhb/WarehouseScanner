using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    #region 用户菜单权限服务
    /// <summary>
    /// 用户菜单权限服务。
    /// 功能要点：
    ///   1. 引入 IMemoryCache 缓存权限，减少磁盘 IO 与反序列化开销
    ///   2. 完整参数 null 校验
    ///   3. 首次加载懒加载（而非构造函数同步加载）
    ///   4. 写操作后主动清除缓存，确保一致性
    /// </summary>
    public class UserMenuPermissionService
    {
        private readonly string _configFilePath;
        private readonly ILogger<UserMenuPermissionService> _logger;
        private readonly IMemoryCache _cache;
        private UserMenuPermissionData? _configData;
        // Singleton 服务被所有请求共享，锁必须是静态的，避免多实例（含测试/重建）各持一把锁
        private static readonly object _syncLock = new();
        private volatile bool _loaded;

        /// <summary>
        /// 配置读写共用的序列化选项。
        /// 与 FactoryConfigService 的统一写法对齐。
        /// 说明：Newtonsoft.Json 默认按属性名不区分大小写匹配、默认不转义非 ASCII 字符，
        ///       与原 PropertyNameCaseInsensitive + UnsafeRelaxedJsonEscaping 的行为一致。
        /// </summary>
        private static readonly JsonSerializerSettings JsonOptions = new()
        {
            Formatting = Formatting.Indented,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public UserMenuPermissionService(
            IWebHostEnvironment environment,
            ILogger<UserMenuPermissionService> logger,
            IMemoryCache cache)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));

            // 配置目录与其余模块统一为 ContentRootPath（与 FactoryConfigService /
            //   ConfigService 等一致）。
            //   发布态下 BaseDirectory 与 ContentRootPath 指向同一目录，读取结果一致。
            var configDir = Path.Combine(
                (environment ?? throw new ArgumentNullException(nameof(environment))).ContentRootPath,
                "config");
            _configFilePath = Path.Combine(configDir, "UserMenuPermissions.json");

            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
        }

        #region 加载/保存
        /// <summary>首次请求懒加载（线程安全的一次性加载）</summary>
        private UserMenuPermissionData EnsureLoaded()
        {
            if (_loaded && _configData != null) return _configData;

            lock (_syncLock)
            {
                if (_loaded && _configData != null) return _configData;

                try
                {
                    if (File.Exists(_configFilePath))
                    {
                        var json = File.ReadAllText(_configFilePath);
                        _configData = JsonConvert.DeserializeObject<UserMenuPermissionData>(json, JsonOptions)
                            ?? new UserMenuPermissionData();

                        _logger.LogInformation("权限加载成功，共 {Count} 条", _configData.Permissions.Count);
                    }
                    else
                    {
                        _configData = new UserMenuPermissionData();
                        SaveConfig();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "加载权限配置失败，回退空集合");
                    _configData = new UserMenuPermissionData();
                }

                _loaded = true;
                return _configData;
            }
        }

        private bool SaveConfig()
        {
            try
            {
                // 原子写入（.tmp + File.Replace），避免崩溃时留下半截 JSON
                //     （读取路径遇损坏文件会回退空权限集合，导致权限配置静默丢失）
                AtomicFileWriter.WriteAtomic(_configFilePath, JsonConvert.SerializeObject(_configData, JsonOptions));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存权限配置失败");
                return false;
            }
        }

        private void InvalidateCache()
        {
            lock (_syncLock)
            {
                _configData = null;
                _loaded = false;
            }
            _cache.Remove(CacheKeys.UserMenuPermissions);
        }
        #endregion

        #region 查询
        /// <summary>获取所有权限（带内存缓存）</summary>
        public List<UserMenuPermission> GetAllPermissions()
        {
            // Singleton 共享可变集合：读取/复制必须在同一把锁内，避免读到写入中途的列表
            lock (_syncLock)
            {
                var data = EnsureLoaded();

                return _cache.GetOrCreate(CacheKeys.UserMenuPermissions, entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
                    entry.SlidingExpiration = TimeSpan.FromMinutes(10);
                    return data.Permissions.ToList();
                })!;
            }
        }

        /// <summary>
        /// 按菜单代码检查权限（对缓存快照做线性扫描，复杂度 O(n)，n = 权限总条数）。
        /// 匹配维度：站点 + 用户 + 菜单。
        ///   - SiteCode 为空的记录 = 全部站点（遗留全局记录的兼容语义）；
        ///   - 同一用户+菜单同时存在站点级与全局记录时，站点级优先（支持按站点收权/放权）。
        /// 仅使用 UserCode 精确匹配——SessionKeys.UserRole 全项目无写入点，
        /// 角色级匹配永远不会命中，故不做角色级匹配。
        /// </summary>
        public bool HasPermission(string userId, string userRole, string menuCode, string siteCode = "")
        {
            // userRole 保留在签名中仅为兼容既有调用方（不参与判定）
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(menuCode))
                return false;

            var allPermissions = GetAllPermissions();
            var normalizedMenu = menuCode.Trim().ToUpperInvariant();
            var normalizedUser = userId.Trim().ToUpperInvariant();
            var normalizedSite = (siteCode ?? string.Empty).Trim();

            // 站点级权限优先，其次全局（SiteCode 为空）记录
            var userPerm = allPermissions.FirstOrDefault(p =>
                    !string.IsNullOrWhiteSpace(p.SiteCode)
                    && string.Equals(p.SiteCode.Trim(), normalizedSite, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.UserCode?.Trim(), normalizedUser, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.MenuCode?.Trim(), normalizedMenu, StringComparison.OrdinalIgnoreCase))
                ?? allPermissions.FirstOrDefault(p =>
                    string.IsNullOrWhiteSpace(p.SiteCode)
                    && string.Equals(p.UserCode?.Trim(), normalizedUser, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.MenuCode?.Trim(), normalizedMenu, StringComparison.OrdinalIgnoreCase));

            return userPerm != null && userPerm.Permission == AppDefaults.PermissionAllowed;
        }
        #endregion

        #region 写操作（均带缓存失效）
        public bool AddPermission(UserMenuPermission permission)
        {
            if (permission == null) throw new ArgumentNullException(nameof(permission));

            try
            {
                bool success;
                lock (_syncLock)
                {
                    var data = EnsureLoaded();
                    var existing = data.Permissions.FirstOrDefault(p =>
                        string.Equals(p.SiteCode, permission.SiteCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.UserCode, permission.UserCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.MenuCode, permission.MenuCode, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        _logger.LogWarning("权限已存在: {SiteCode} - {UserCode} - {MenuCode}",
                            permission.SiteCode, permission.UserCode, permission.MenuCode);
                        return false;
                    }

                    data.Permissions.Add(permission);
                    success = SaveConfig();
                }

                if (success) InvalidateCache();
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加权限时发生错误");
                return false;
            }
        }

        public bool UpdatePermission(string originalUserCode, string originalMenuCode,
            UserMenuPermission permission, string originalSiteCode = "")
        {
            if (permission == null) throw new ArgumentNullException(nameof(permission));

            try
            {
                bool success;
                lock (_syncLock)
                {
                    var data = EnsureLoaded();
                    var index = data.Permissions.FindIndex(p =>
                        string.Equals(p.SiteCode, originalSiteCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.UserCode, originalUserCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.MenuCode, originalMenuCode, StringComparison.OrdinalIgnoreCase));

                    if (index < 0) return false;

                    data.Permissions[index] = permission;
                    success = SaveConfig();
                }

                if (success) InvalidateCache();
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新权限时发生错误");
                return false;
            }
        }

        public bool DeletePermission(string userCode, string menuCode, string siteCode = "")
        {
            try
            {
                bool success;
                lock (_syncLock)
                {
                    var data = EnsureLoaded();
                    var permission = data.Permissions.FirstOrDefault(p =>
                        string.Equals(p.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.UserCode, userCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.MenuCode, menuCode, StringComparison.OrdinalIgnoreCase));

                    if (permission == null) return false;

                    data.Permissions.Remove(permission);
                    success = SaveConfig();
                }

                if (success) InvalidateCache();
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除权限时发生错误");
                return false;
            }
        }

        public List<UserMenuPermission> SearchPermissions(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm)) return GetAllPermissions();

            searchTerm = searchTerm.Trim().ToLowerInvariant();
            return GetAllPermissions().Where(p =>
                (p.SiteCode ?? string.Empty).ToLowerInvariant().Contains(searchTerm)
                || (p.UserCode ?? string.Empty).ToLowerInvariant().Contains(searchTerm)
                || (p.UserName ?? string.Empty).ToLowerInvariant().Contains(searchTerm)
                || (p.UserGroup ?? string.Empty).ToLowerInvariant().Contains(searchTerm)
                || (p.MenuName ?? string.Empty).ToLowerInvariant().Contains(searchTerm)
                || (p.Remark ?? string.Empty).ToLowerInvariant().Contains(searchTerm)).ToList();
        }
        #endregion
    }
    #endregion
}
