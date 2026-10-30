using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    public class ConfigService
    {
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConfigService> _logger;
        private readonly EncryptionHelper _encryption;
        private readonly IMemoryCache _cache;

        // 数据库配置缓存键（缓存解密后的 DatabaseConfig，避免高频查询重复读盘 + AES 解密）
        private const string ConfigCacheKey = "ConfigService:DatabaseConfig";

        public ConfigService(IWebHostEnvironment environment, IConfiguration configuration, ILogger<ConfigService> logger, EncryptionHelper encryption, IMemoryCache cache)
        {
            _environment = environment;
            _configuration = configuration;
            _logger = logger;
            _encryption = encryption;
            _cache = cache;
        }

        public async Task<DatabaseConfig> LoadConfigAsync()
        {
            // 缓存解密后的配置（避免 ShippingSchedule 等高频查询每次读盘 + AES 解密），
            //       返回副本以防调用方改写（如 ConfigController 回显掩码）污染缓存。
            var cached = await _cache.GetOrCreateAsync(ConfigCacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                return LoadConfigFromDiskAsync();
            });

            return new DatabaseConfig
            {
                EncryptedConnectionString = (cached?.EncryptedConnectionString)!,
                ConnectionString = (cached?.ConnectionString)!
            };
        }

        private async Task<DatabaseConfig> LoadConfigFromDiskAsync()
        {
            try
            {
                string configPath = GetConfigFilePath();

                if (!File.Exists(configPath))
                {
                    _logger.LogWarning("配置文件不存在: {ConfigPath}", configPath);
                    return new DatabaseConfig();
                }

                string jsonContent = await File.ReadAllTextAsync(configPath);
                var fileConfig = JsonConvert.DeserializeObject<DatabaseConfig>(jsonContent);

                var config = new DatabaseConfig
                {
                    EncryptedConnectionString = (fileConfig?.EncryptedConnectionString)!
                };

                if (!string.IsNullOrEmpty(config.EncryptedConnectionString))
                {
                    try
                    {
                        config.ConnectionString = _encryption.Decrypt(config.EncryptedConnectionString);
                    }
                    catch (Exception ex)
                    {
                        // 解密失败（密钥缺失/更换）不能把密文当作明文连接串返回，
                        // 否则上层会用一段无效字符串去连库，错误原因被彻底掩盖。
                        _logger.LogError(ex, "连接字符串解密失败（密钥可能未配置或已更换），按加载失败处理");
                        config.ConnectionString = string.Empty;
                    }
                }

                _logger.LogInformation("配置文件加载成功: {ConfigPath}", configPath);
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载配置文件失败");
                return new DatabaseConfig();
            }
        }

        public async Task<bool> SaveConfigAsync(DatabaseConfig config)
        {
            try
            {
                string plainText = config.ConnectionString?.Trim() ?? "";
                if (string.IsNullOrEmpty(plainText))
                {
                    _logger.LogWarning("连接字符串为空，拒绝保存");
                    return false;
                }

                config.EncryptedConnectionString = _encryption.Encrypt(plainText);

                string configPath = GetConfigFilePath();
                var directory = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                var jsonSettings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore,
                    ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                    {
                        NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy()
                    }
                };

                string jsonContent = JsonConvert.SerializeObject(config, jsonSettings);
                // 原子写入（.tmp + File.Replace），进程崩溃/断电不会写坏原配置文件
                await AtomicFileWriter.WriteAtomicAsync(configPath, jsonContent);

                _cache.Remove(ConfigCacheKey);
                _logger.LogInformation("配置文件保存成功: {ConfigPath}", configPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存配置文件失败");
                return false;
            }
        }

        public bool TestConnection(string connectionString)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    _logger.LogInformation("数据库连接测试成功");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库连接测试失败");
                return false;
            }
        }

        private string GetConfigFilePath()
        {
            return Path.Combine(_environment.ContentRootPath, "config", "database.config.json");
        }
    }
}
