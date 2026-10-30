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
    /// <summary>管理页面 CRUD</summary>
    public partial class FactoryConfigService
    {
        #region ========== 管理页面 CRUD（异步 API） ==========

        /// <summary>
        /// 异步加载所有工厂（含未启用项），返回解密后的副本列表供管理页面展示。
        /// </summary>
        public async Task<FactoriesConfig> LoadFactoriesAsync()
        {
            var config = await EnsureLoadedAsync();
            return new FactoriesConfig
            {
                Factories = config.Factories.Select(f => CloneDecrypt(f)).ToList()
            };
        }

        public async Task<bool> SaveFactoriesAsync(FactoriesConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var success = await SaveConfigInternalAsync(config);
            if (success) InvalidateCache();
            return success;
        }

        /// <summary>
        /// 按代码获取工厂（异步，管理页面用），返回解密后的副本。
        /// </summary>
        public async Task<Factory?> GetFactoryByCodeAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var config = await EnsureLoadedAsync();
            var raw = config.Factories.FirstOrDefault(f =>
                string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
            if (raw == null) return null;

            return CloneDecrypt(raw);
        }

        public async Task<bool> AddFactoryAsync(Factory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            return await MutateConfigAsync(config =>
            {
                if (config.Factories.Any(f =>
                    string.Equals(f.Code, factory.Code, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("工厂代码已存在: {Code}", factory.Code);
                    return false;
                }

                if (HasDuplicateCompanyPlant(config.Factories, factory, excludeCode: null))
                    return false;

                config.Factories.Add(factory);
                return true;
            });
        }

        public async Task<bool> UpdateFactoryAsync(string originalCode, Factory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (string.IsNullOrWhiteSpace(originalCode)) return false;

            return await MutateConfigAsync(config =>
            {
                var index = config.Factories.FindIndex(f =>
                    string.Equals(f.Code, originalCode, StringComparison.OrdinalIgnoreCase));

                if (index < 0)
                {
                    _logger.LogWarning("要更新的工厂不存在: {Code}", originalCode);
                    return false;
                }

                if (HasDuplicateCompanyPlant(config.Factories, factory, excludeCode: originalCode))
                    return false;

                config.Factories[index] = factory;
                return true;
            });
        }

        /// <summary>
        /// 在写锁内「读-改-写」配置副本。
        /// 若直接修改 <c>_loadedConfig</c> 里的共享 <see cref="List{T}"/>：并发枚举会看到半更新状态
        /// （Query 路径正在遍历同一实例），两个并发写会互相覆盖导致丢更新。
        /// 副本用 JSON 往返复制，避免手工逐字段拷贝漏掉某个字段导致站点信息静默丢失。
        /// 注意：锁内不得调用 <c>EnsureLoadedAsync</c>（SemaphoreSlim 非重入，会死锁）。
        /// </summary>
        private async Task<bool> MutateConfigAsync(Func<FactoriesConfig, bool> mutate)
        {
            bool acquired = false;
            try
            {
                await _asyncLock.WaitAsync();
                acquired = true;

                // 锁内取当前快照：缓存可能刚被其它写路径置空，此时必须重新读盘，
                // 否则会基于陈旧快照写回，覆盖掉别人的改动。
                var current = _loadedConfig ?? (_loadedConfig = await LoadFromDiskAsync());
                var copy = JsonConvert.DeserializeObject<FactoriesConfig>(
                    JsonConvert.SerializeObject(current, JsonOptions), JsonOptions) ?? new FactoriesConfig();

                if (!mutate(copy)) return false;

                var success = await SaveConfigInternalAsync(copy);
                if (success) InvalidateCache();
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "修改工厂配置失败");
                return false;
            }
            finally
            {
                if (acquired) _asyncLock.Release();
            }
        }

        /// <summary>
        /// 校验 Company+Plant 组合唯一：两个条目 生效公司+工厂 相同会让实例缓存键等价，
        /// 导致共享同一 Epicor 会话，属配置错误。
        /// </summary>
        private bool HasDuplicateCompanyPlant(IEnumerable<Factory> existing, Factory candidate, string? excludeCode)
        {
            string candidateKey = $"{candidate.EffectiveCompany}|{candidate.Plant}".ToLowerInvariant();
            bool duplicate = existing.Any(f =>
                !string.Equals(f.Code, excludeCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals($"{f.EffectiveCompany}|{f.Plant}".ToLowerInvariant(), candidateKey, StringComparison.Ordinal));
            if (duplicate)
            {
                _logger.LogWarning("站点 {Code} 的 公司+工厂 组合与其他站点重复: {Company}|{Plant}",
                    candidate.Code, candidate.EffectiveCompany, candidate.Plant);
            }
            return duplicate;
        }

        public async Task<bool> DeleteFactoryAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;

            return await MutateConfigAsync(config =>
            {
                var factory = config.Factories.FirstOrDefault(f =>
                    string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));

                if (factory == null)
                {
                    _logger.LogWarning("要删除的工厂不存在: {Code}", code);
                    return false;
                }

                config.Factories.Remove(factory);
                return true;
            });
        }
        #endregion
    }
}