using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WarehouseScanner.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    /// <summary>Epicor 业务服务工厂接口</summary>
    public interface IEpicorBusinessServiceFactory
    {
        /// <summary>基于当前 HttpContext.Session 创建 EpicorIssueService</summary>
        Task<EpicorIssueService> CreateIssueServiceAsync();

        /// <summary>基于当前 HttpContext.Session 创建 EpicorReturnService</summary>
        Task<EpicorReturnService> CreateReturnServiceAsync();

        /// <summary>基于当前 HttpContext.Session 创建 EpicorReceiptService</summary>
        Task<EpicorReceiptService> CreateReceiptServiceAsync(string operatorId = "");

        /// <summary>基于当前 HttpContext.Session 创建 EpicorAuthService</summary>
        EpicorAuthService CreateAuthService();
    }

    /// <summary>
    /// Epicor 业务服务工厂实现
    /// 复用 IEpicorAuthServiceFactory.CreateFromSession 统一创建路径
    /// </summary>
    public class EpicorBusinessServiceFactory : IEpicorBusinessServiceFactory
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IEpicorAuthServiceFactory _authServiceFactory;
        private readonly IDataProtector _protector;
        private readonly ILoggerFactory _loggerFactory;

        public EpicorBusinessServiceFactory(
            IHttpContextAccessor httpContextAccessor,
            IEpicorAuthServiceFactory authServiceFactory,
            IDataProtectionProvider protectionProvider,
            ILogger<EpicorBusinessServiceFactory> logger,
            ILoggerFactory loggerFactory)
        {
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            _authServiceFactory = authServiceFactory ?? throw new ArgumentNullException(nameof(authServiceFactory));
            _loggerFactory = loggerFactory;
            _protector = (protectionProvider ?? throw new ArgumentNullException(nameof(protectionProvider)))
                .CreateProtector("AccountController.SensitiveData");
        }

        /// <summary>基于 Session 创建 EpicorAuthService（所有业务服务的底层）</summary>
        public EpicorAuthService CreateAuthService()
        {
            var session = _httpContextAccessor.GetRequiredSession();
            return _authServiceFactory.CreateFromSession(session, _protector);
        }

        /// <summary>创建发料服务（公司/工厂以已解析的 auth 实例为准，兼容旧会话兜底）</summary>
        public Task<EpicorIssueService> CreateIssueServiceAsync()
        {
            var auth = CreateAuthService();
            var issueLogger = _loggerFactory?.CreateLogger<EpicorIssueService>();
            return Task.FromResult(new EpicorIssueService(auth, auth.Company, issueLogger));
        }

        /// <summary>创建退回服务</summary>
        public Task<EpicorReturnService> CreateReturnServiceAsync()
        {
            var auth = CreateAuthService();
            var returnLogger = _loggerFactory?.CreateLogger<EpicorReturnService>();
            return Task.FromResult(new EpicorReturnService(auth, auth.Company, returnLogger));
        }

        /// <summary>创建入库服务</summary>
        public Task<EpicorReceiptService> CreateReceiptServiceAsync(string operatorId = "")
        {
            var auth = CreateAuthService();
            var receiptLogger = _loggerFactory?.CreateLogger<EpicorReceiptService>();
            return Task.FromResult(new EpicorReceiptService(auth, auth.Company, operatorId, receiptLogger, auth.Plant));
        }
    }
}
