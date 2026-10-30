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
    /// <summary>核心加载 / 保存</summary>
    public partial class FactoryConfigService
    {
        #region ========== 核心加载/保存 ==========

        /// <summary>
        /// 首次请求懒加载（同步，向后兼容）。
        /// 不在请求线程上无界阻塞：最多等待 5 秒；超时后直接返回现有配置，
        /// 刷新交由既有异步加载路径 <see cref="EnsureLoadedAsync"/>（不新增后台线程）。
        /// 应用启动阶段会调用 <see cref="WarmUpAsync"/> 预热，使请求期同步路径基本只做内存命中。
        /// </summary>
        private FactoriesConfig EnsureLoaded()
        {
            var cached = _loadedConfig;
            if (cached != null) return cached;

            bool acquired = false;
            try
            {
                acquired = _asyncLock.Wait(TimeSpan.FromSeconds(5));
                if (!acquired)
                {
                    // 超时降级：不同步 LoadFromDisk()（会二次阻塞请求线程），
                    // 直接返回现有配置；若尚未加载则返回默认配置，由异步路径后续刷新。
                    if (Interlocked.Exchange(ref _syncLoadTimeoutWarned, 1) == 0)
                        _logger.LogWarning("工厂配置首次加载等待超时，返回现有配置（刷新交由异步加载路径）Path:{Path}",
                            _configFilePath);
                    return _loadedConfig ?? GetDefaultFactoriesConfig();
                }

                if (_loadedConfig != null)
                    return _loadedConfig;

                _loadedConfig = LoadFromDisk();
                return _loadedConfig;
            }
            finally
            {
                if (acquired) _asyncLock.Release();
            }
        }

        /// <summary>
        /// 配置完整性校验：启用站点必须显式配置 Company 与 Plant（以及 Code/ApiUrl）。
        /// 多站点隔离（CallSettings、@Plant 过滤、锁/幂等键）全部依赖这两个值，
        /// 缺失时宁可在启动/加载期显式失败，也绝不静默回退默认值或硬编码兜底。
        /// </summary>
        private static void ValidateConfig(FactoriesConfig config)
        {
            var invalid = config.Factories
                .Where(f => f.Enabled && (
                    string.IsNullOrWhiteSpace(f.Code) ||
                    string.IsNullOrWhiteSpace(f.Company) ||
                    string.IsNullOrWhiteSpace(f.Plant) ||
                    string.IsNullOrWhiteSpace(f.ApiUrl)))
                .Select(f => string.IsNullOrWhiteSpace(f.Code) ? "(站点代码为空)" : f.Code)
                .ToList();

            if (invalid.Count > 0)
                throw new InvalidOperationException(
                    $"factories.json 配置不完整：启用站点 [{string.Join(", ", invalid)}] 缺少 Company/Plant/ApiUrl 必填项。" +
                    "请在管理界面或配置文件补全后重启（禁止默认值兜底，避免多站点串数据）。");
        }

        /// <summary>首次请求懒加载（异步，供管理页面与启动预热使用）</summary>
        private async Task<FactoriesConfig> EnsureLoadedAsync()
        {
            var cached = _loadedConfig;
            if (cached != null) return cached;

            await _asyncLock.WaitAsync();
            try
            {
                if (_loadedConfig != null) return _loadedConfig;
                _loadedConfig = await LoadFromDiskAsync();
                return _loadedConfig;
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        /// <summary>
        /// 启动阶段异步预热。
        /// 由 Program.cs 在应用启动时调用，让后续请求路径上的同步 EnsureLoaded()
        /// 只做内存命中，不触发同步文件 I/O。失败不阻塞启动。
        /// </summary>
        public async Task WarmUpAsync()
        {
            try
            {
                var config = await EnsureLoadedAsync();
                _logger.LogInformation("工厂配置预热完成，共 {Count} 个站点", config.Factories.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工厂配置预热失败（不阻塞启动，后续请求将按需懒加载）");
            }
        }

        /// <summary>从磁盘读取配置（同步，仅首次加载时调用一次）</summary>
        private FactoriesConfig LoadFromDisk()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var config = JsonConvert.DeserializeObject<FactoriesConfig>(json, JsonOptions)
                        ?? new FactoriesConfig();
                    ValidateConfig(config);

                    _logger.LogInformation("工厂配置加载成功，共 {Count} 个工厂 Path:{Path}",
                        config.Factories.Count, _configFilePath);
                    return config;
                }

                _logger.LogWarning("工厂配置文件不存在，生成空默认模板（站点默认停用，需补全 Company/Plant/ApiUrl 后启用）Path:{Path}", _configFilePath);
                var defaultConfig = GetDefaultFactoriesConfig();
                SaveConfigInternal(defaultConfig);
                return defaultConfig;
            }
            catch (InvalidOperationException)
            {
                // 配置校验失败必须显式暴露（携带站点代码与缺失字段），
                // 绝不能落进下方 catch 静默回退默认配置——那等于用兜底掩盖必填项缺失。
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载工厂配置失败，回退默认配置");
                return GetDefaultFactoriesConfig();
            }
        }

        /// <summary>从磁盘读取配置（异步）</summary>
        private async Task<FactoriesConfig> LoadFromDiskAsync()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = await File.ReadAllTextAsync(_configFilePath);
                    var config = JsonConvert.DeserializeObject<FactoriesConfig>(json, JsonOptions)
                        ?? new FactoriesConfig();
                    ValidateConfig(config);

                    _logger.LogInformation("工厂配置加载成功，共 {Count} 个工厂 Path:{Path}",
                        config.Factories.Count, _configFilePath);
                    return config;
                }

                _logger.LogWarning("工厂配置文件不存在，生成空默认模板（站点默认停用，需补全 Company/Plant/ApiUrl 后启用）Path:{Path}", _configFilePath);
                var defaultConfig = GetDefaultFactoriesConfig();
                await SaveConfigInternalAsync(defaultConfig);
                return defaultConfig;
            }
            catch (InvalidOperationException)
            {
                // 与同步路径同口径：配置校验失败显式抛出，不回退默认配置
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载工厂配置失败，回退默认配置");
                return GetDefaultFactoriesConfig();
            }
        }

        private bool SaveConfigInternal(FactoriesConfig config)
        {
            try
            {
                // 临时文件名带 GUID（AtomicFileWriter）：并发写入各用独立临时文件，
                // 不会互相覆盖，进程中途崩溃也只会留下可回收的 .tmp 残留。
                AtomicFileWriter.WriteAtomic(_configFilePath, JsonConvert.SerializeObject(config, JsonOptions));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存工厂配置失败 Path:{Path}", _configFilePath);
                return false;
            }
        }

        private async Task<bool> SaveConfigInternalAsync(FactoriesConfig config)
        {
            try
            {
                var json = JsonConvert.SerializeObject(config, JsonOptions);
                await AtomicFileWriter.WriteAtomicAsync(_configFilePath, json);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存工厂配置失败 Path:{Path}", _configFilePath);
                return false;
            }
        }

        private void InvalidateCache()
        {
            // 此处只做一次引用置空，无需 _asyncLock.Wait() 阻塞请求线程——
            // volatile 写本身是原子的，也不会与异步加载路径产生数据竞争。
            _loadedConfig = null;
            _cache.Remove(CacheKeys.FactoryConfig);
        }

        /// <summary>
        /// 默认模板：仅在 factories.json 缺失时生成一次。
        /// 站点默认停用——Company/Plant/ApiUrl 均为必填项，禁止伪造默认值，
        /// 管理员必须在管理界面补全并显式启用后站点才可用。
        /// </summary>
        private FactoriesConfig GetDefaultFactoriesConfig()
        {
            return new FactoriesConfig
            {
                Factories = new List<Factory>
                {
                    new Factory
                    {
                        Code = "DG",
                        Name = "GCM Dongguan",
                        NameCN = "GCM东莞",
                        Description = "Main manufacturing facility in Dongguan",
                        DescriptionCN = "主要制造工厂在东莞",
                        Location = "Dongguan, China",
                        Icon = "fa-home",
                        ApiUrl = string.Empty,
                        ApiKey = string.Empty,
                        Enabled = false
                    },
                    new Factory
                    {
                        Code = "US1",
                        Name = "US San Francisco",
                        NameCN = "美国旧金山",
                        Description = "US San Francisco",
                        DescriptionCN = "美国旧金山",
                        Location = "Houston, Texas, USA",
                        Icon = "fa-flag-usa",
                        ApiUrl = string.Empty,
                        ApiKey = string.Empty,
                        Enabled = false
                    },
                    new Factory
                    {
                        Code = "US2",
                        Name = "US Chicago",
                        NameCN = "芝加哥",
                        Description = "US Chicago",
                        DescriptionCN = "芝加哥",
                        Location = "Los Angeles, California, USA",
                        Icon = "fa-industry",
                        ApiUrl = string.Empty,
                        ApiKey = string.Empty,
                        Enabled = false
                    }
                }
            };
        }

        /// <summary>
        /// 将磁盘存储的原始 Factory 复制一份并对敏感字段解密：
        ///   - ApiKey：AES 解密（旧数据为明文时 TryDecrypt 自动回退原值）
        /// 调用方拿到的副本永远是明文可用状态，修改副本不会污染缓存。
        /// </summary>
        private Factory CloneDecrypt(Factory raw)
        {
            return new Factory
            {
                Code = raw.Code,
                Name = raw.Name,
                NameCN = raw.NameCN,
                Description = raw.Description,
                DescriptionCN = raw.DescriptionCN,
                Location = raw.Location,
                Company = raw.Company,
                Plant = raw.Plant,
                Icon = raw.Icon,
                ApiUrl = raw.ApiUrl,
                ApiKey = _encryption.TryDecrypt(raw.ApiKey),
                Enabled = raw.Enabled
            };
        }

        /// <summary>
        /// 语言感知的字段选择（中/英文名、描述），同时解密敏感字段。
        /// </summary>
        private Factory CloneForLanguage(Factory raw, bool isZh)
        {
            var f = CloneDecrypt(raw);
            f.Name = isZh && !string.IsNullOrEmpty(raw.NameCN) ? raw.NameCN : raw.Name;
            f.Description = isZh && !string.IsNullOrEmpty(raw.DescriptionCN) ? raw.DescriptionCN : raw.Description;
            return f;
        }
        #endregion
    }
}