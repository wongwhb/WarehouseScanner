using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;

namespace WarehouseScannerCore.Utilities
{
    #region Epicor 会话辅助类
    /// <summary>
    /// Epicor 会话辅助类
    /// 职责：统一管理从 HttpContext.Session 读取/写入 Epicor 连接凭证（加密存储）
    /// 目标：消除控制器和业务服务中分散的 Session.GetString("Password") 魔法字符串
    /// </summary>
    public static class EpicorSessionHelper
    {
        /// <summary>从 Session 读取用户名</summary>
        public static string GetUsername(ISession session)
            => session.GetString(SessionKeys.Username) ?? string.Empty;

        /// <summary>站点代码。优先新键；旧会话回退历史键（其值即站点代码）。</summary>
        public static string GetSiteCode(ISession session)
            => session.GetString(SessionKeys.SiteCode) is { Length: > 0 } site
                ? site
                : session.GetString(SessionKeys.CompanyCode) ?? string.Empty;

        /// <summary>Epicor 公司代码</summary>
        public static string GetEpicorCompany(ISession session)
            => session.GetString(SessionKeys.EpicorCompany) ?? string.Empty;

        /// <summary>Epicor 工厂(Plant)，空串 = 不限定工厂</summary>
        public static string GetPlant(ISession session)
            => session.GetString(SessionKeys.Plant) ?? string.Empty;

        /// <summary>
        /// 租户键：站点|公司|工厂（小写）。所有应用级缓存/锁/幂等键统一由此派生。
        /// 公司/工厂由 FactoryConfigService.ResolveTarget 解析，与认证实例缓存键同源：
        /// 旧会话（无 EpicorCompany/Plant 键）不会再派生出与实例缓存不同的键，避免锁互斥与幂等失效。
        /// 站点段保证手工编辑 factories.json 造成 公司+工厂 重复时仍不串站点。
        /// </summary>
        public static string GetTenantKey(ISession session, FactoryConfigService factoryConfigService)
        {
            var target = factoryConfigService.ResolveTarget(
                GetSiteCode(session), GetEpicorCompany(session), GetPlant(session));
            return $"{target.SiteCode}|{target.Company}|{target.Plant}".ToLowerInvariant();
        }

        /// <summary>写入凭证到 Session（密码/API Key 加密；站点三元组原子写入）</summary>
        public static void SetCredentials(
            ISession session,
            string username,
            string password,
            string apiUrl,
            string apiKey,
            EpicorSiteTarget target,
            IDataProtector protector)
        {
            session.SetString(SessionKeys.Username, username ?? string.Empty);
            session.SetString(SessionKeys.ApiUrl, apiUrl ?? string.Empty);
            session.SetString(SessionKeys.SiteCode, target.SiteCode);
            session.SetString(SessionKeys.EpicorCompany, target.Company);
            session.SetString(SessionKeys.Plant, target.Plant);
            // 兼容旧读取方（历史键语义 = 站点代码）
            session.SetString(SessionKeys.CompanyCode, target.SiteCode);
            if (!string.IsNullOrEmpty(password))
                session.SetString(SessionKeys.Password, protector.Protect(password));
            if (!string.IsNullOrEmpty(apiKey))
                session.SetString(SessionKeys.ApiKey, protector.Protect(apiKey));
        }
    }
    #endregion

    #region IHttpContextAccessor 扩展方法
    /// <summary>IHttpContextAccessor 扩展方法：业务服务层便捷读取会话</summary>
    public static class HttpContextAccessorExtensions
    {
        /// <summary>安全获取 Session（为 null 时抛 UnauthorizedAccessException）</summary>
        public static ISession GetRequiredSession(this IHttpContextAccessor accessor)
        {
            var session = accessor.HttpContext?.Session;
            if (session == null)
                throw new UnauthorizedAccessException("会话不可用，请重新登录");
            return session;
        }
    }
    #endregion
}
