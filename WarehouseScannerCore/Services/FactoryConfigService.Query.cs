// Services/FactoryConfigService.cs
// 统一工厂配置服务：
//   1. 改用 Models.Factory / Models.FactoriesConfig（与 JsonFileService 同一模型）
//   2. 统一路径：ContentRootPath/config/factories.json
//   3. 引入 IMemoryCache 缓存，避免重复文件 I/O
//   4. 保留同步 API（GetFactories/GetFactory）+ 提供异步 API（供管理页面 CRUD）
//   5. ApiKey 在磁盘上加密存储，查询时统一解密返回；
//      保存时由调用方（FactoryController）加密后传入。
using Microsoft.Extensions.Caching.Memory;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    /// <summary>查询 API</summary>
    public partial class FactoryConfigService
    {
        #region ========== 查询 API（带语言切换 + 内存缓存） ==========

        /// <summary>
        /// 启用工厂的原始列表（缓存 30 分钟、滑动 10 分钟；不解密 ApiKey、不做语言切换）。
        /// 所有查询入口共用，避免多份相同的缓存引导代码。
        /// </summary>
        private List<Factory> GetEnabledRawFactories()
            => _cache.GetOrCreate(CacheKeys.FactoryConfig, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
                entry.SlidingExpiration = TimeSpan.FromMinutes(10);
                return EnsureLoaded().Factories.Where(f => f.Enabled).ToList();
            })!;

        /// <summary>获取所有启用工厂（带语言切换，返回只读视图，敏感字段已解密）</summary>
        public List<Factory> GetFactories(string language = "zh-CN")
        {
            // 缓存中存的是原始（启用状态）引用，不做解密/语言切换，避免缓存污染
            var rawFactories = GetEnabledRawFactories();

            bool isZh = string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase);
            return rawFactories.Select(f => CloneForLanguage(f, isZh)).ToList();
        }

        /// <summary>
        /// 获取所有启用工厂的轻量视图（仅 Code/Name），不复制、不解密 ApiKey。
        /// 供登录页等只需要站点代码的场景使用，避免把明文 ApiKey 带出服务层。
        /// </summary>
        public List<FactoryOption> GetFactoryOptions(string language = "zh-CN")
        {
            var rawFactories = GetEnabledRawFactories();

            bool isZh = string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase);
            return rawFactories
                .Select(f => new FactoryOption(f.Code, isZh && !string.IsNullOrEmpty(f.NameCN) ? f.NameCN : f.Name))
                .ToList();
        }

        /// <summary>
        /// 解析站点调用上下文（站点/公司/工厂）。
        /// 配置是"站点 → 公司/工厂"的唯一事实来源：命中启用工厂时一律采用配置值，
        /// 会话旧值（发布前登录、改配置未重新登录）自动纠正——租户键、实例缓存键、
        /// 登出清理三处共用本方法，天然同源，消除新旧会话的键间隙。
        /// 站点不在启用列表（被禁用/删除）时保留会话值、公司回退站点代码（既有退化语义）。
        /// 仅读内存缓存，不解密 ApiKey、不复制对象，可高频调用。
        /// </summary>
        public EpicorSiteTarget ResolveTarget(string siteCode, string company, string plant)
        {
            siteCode ??= string.Empty;
            if (string.IsNullOrWhiteSpace(siteCode))
                return new EpicorSiteTarget(siteCode, company ?? string.Empty, plant ?? string.Empty);

            var factory = GetEnabledRawFactories().FirstOrDefault(f =>
                string.Equals(f.Code, siteCode, StringComparison.OrdinalIgnoreCase));
            if (factory != null)
                return new EpicorSiteTarget(siteCode, factory.EffectiveCompany, factory.Plant);

            return new EpicorSiteTarget(
                siteCode,
                string.IsNullOrEmpty(company) ? siteCode : company,
                plant ?? string.Empty);
        }

        /// <summary>按代码获取工厂（带语言切换，敏感字段已解密）</summary>
        public Factory? GetFactory(string code, string language = "zh-CN")
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            // 从语言切换列表查找（GetFactories 已只返回启用站点并完成解密）
            var factories = GetFactories(language);
            return factories.FirstOrDefault(f =>
                string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 获取指定站点磁盘上存储的原始（加密）ApiKey。
        /// 用于编辑页面"ApiKey 保持掩码 → 保留原加密值"场景，避免把掩码写坏磁盘数据。
        /// </summary>
        public string? GetRawApiKey(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var config = EnsureLoaded();
            var raw = config.Factories.FirstOrDefault(f =>
                string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
            return raw?.ApiKey;
        }

        /// <summary>强制重新加载（测试/管理用）</summary>
        public void ReloadConfig()
        {
            InvalidateCache();
        }
        #endregion
    }
}