// Services/FactoryConfigService.cs
// 统一工厂配置服务：
//   1. 改用 Models.Factory / Models.FactoriesConfig（与 JsonFileService 同一模型）
//   2. 统一路径：ContentRootPath/config/factories.json
//   3. 引入 IMemoryCache 缓存，避免重复文件 I/O
//   4. 保留同步 API（GetFactories/GetFactory）+ 提供异步 API（供管理页面 CRUD）
//   5. ApiKey 在磁盘上加密存储，查询时统一解密返回；
//      保存时由调用方（FactoryController）加密后传入。
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 工厂配置服务（统一版），负责加载、缓存、CRUD 工厂配置
    /// </summary>
    public partial class FactoryConfigService
    {
        private readonly string _configFilePath;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<FactoryConfigService> _logger;
        private readonly IMemoryCache _cache;
        private readonly EncryptionHelper _encryption;
        // 使用 SemaphoreSlim 代替 lock/Monitor：支持 WaitAsync()，无线程亲和性，await 后可安全 Release
        private readonly SemaphoreSlim _asyncLock = new(1, 1);

        // volatile 保证跨线程可见性，避免同步路径读到过期的 null 而反复进入锁
        private volatile FactoriesConfig? _loadedConfig;

        // 同步加载超时告警只记录一次，避免锁竞争期间每个请求都刷日志
        private int _syncLoadTimeoutWarned;

        /// <summary>
        /// 配置读写共用的序列化选项（保存与加载必须使用同一套命名 / 序列化策略）。
        /// 说明：Formatting.Indented 只在写入时生效，MissingMemberHandling.Ignore 只在读取时生效，
        ///       合并为单实例不会产生副作用。
        /// </summary>
        private static readonly JsonSerializerSettings JsonOptions = new()
        {
            Formatting = Formatting.Indented,
            // 显式忽略未知字段：防止 factories.json 中残留的历史字段导致反序列化失败。
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        /// <summary>
        /// 构造函数（不触发同步文件 I/O，首次请求时懒加载）
        /// </summary>
        public FactoryConfigService(
            IWebHostEnvironment environment,
            ILogger<FactoryConfigService> logger,
            IMemoryCache cache,
            EncryptionHelper encryption)
        {
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));

            var configDir = Path.Combine(_environment.ContentRootPath, "config");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

            _configFilePath = Path.Combine(configDir, "factories.json");
        }

    }
}