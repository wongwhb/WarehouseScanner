using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WarehouseScanner.Services;

namespace WarehouseScannerCore.Services
{
    public class ShippingScheduleResult
    {
        public string OrderNum { get; set; } = string.Empty;
        public int OrderLine { get; set; }
        public int OrderRelNum { get; set; }
        /// <summary>对应的工单号（ShippingSchedule.JobNum）—— 用于回填"批次号"输入框</summary>
        public string JobNum { get; set; } = string.Empty;
        /// <summary>计划出货数量（BAQ SO_SchdUpdate 的 OrderRel_SellingReqQty）</summary>
        public decimal PlanShipQty { get; set; }
        /// <summary>已发货数量（BAQ SO_SchdUpdate 的 Calculated_reltotalqty）</summary>
        public decimal ShippedQty { get; set; }
    }

    public class ShippingScheduleQueryService
    {
        private readonly IEpicorAuthServiceFactory _authServiceFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IDataProtector _protector;
        private readonly ILogger<ShippingScheduleQueryService> _logger;

        public ShippingScheduleQueryService(
            IEpicorAuthServiceFactory authServiceFactory,
            IHttpContextAccessor httpContextAccessor,
            IDataProtectionProvider protectionProvider,
            ILogger<ShippingScheduleQueryService> logger)
        {
            _authServiceFactory = authServiceFactory ?? throw new ArgumentNullException(nameof(authServiceFactory));
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            _protector = (protectionProvider ?? throw new ArgumentNullException(nameof(protectionProvider)))
                .CreateProtector("AccountController.SensitiveData");
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 查询发货计划（PONum + OrderLine + OrderRelNum，BAQ SO_SchdUpdate）。
        /// 返回值二元组把「接口故障」与「查无记录」严格区分：
        ///   - apiError 非空：Epicor 接口/网络故障，调用方必须按「接口失败」提示并透传真实错误；
        ///   - apiError 为空且 result 为空：确实查不到该发货行，调用方按「未找到发货计划」提示。
        /// 早前实现把两者都收敛为 null，导致 BAQ 故障被误报为「未找到发货计划」。
        /// </summary>
        public async Task<(ShippingScheduleResult? result, string? apiError)> QueryByJobAndPartAsync(
            string poNum, int orderLine, int orderRelNum)
        {
            if (string.IsNullOrWhiteSpace(poNum) || orderLine < 0 || orderRelNum < 0)
            {
                _logger.LogWarning("QueryByJobAndPartAsync: 参数非法 PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum}",
                    poNum, orderLine, orderRelNum);
                return (null, null); // 参数非法：按"未找到"口径返回，不冒充接口故障
            }

            // 改为从 Epicor BAQ SO_SchdUpdate 读取数据（传入 PONum/OrderLine/OrderRelNum），
            // 不查询本地 ShippingSchedule 表。
            try
            {
                var session = _httpContextAccessor.HttpContext?.Session;
                if (session == null)
                {
                    _logger.LogError("HttpContext.Session 不可用，无法调用 BAQ SO_SchdUpdate");
                    return (null, "会话不可用，无法查询发货计划");
                }

                var authService = _authServiceFactory.CreateFromSession(session, _protector);
                var result = await authService.QuerySOSchdUpdateByBAQAsync(poNum, orderLine, orderRelNum);

                // 必须读本次调用所属 authService 的错误状态：底层把接口故障与"无结果"都返回 null，
                // 不看错误状态会让接口故障被上层误报为"未找到发货计划"，并据此清空操作员输入的字段
                if (authService.HasApiError)
                    return (null, authService.LastApiError ?? "Epicor API 调用失败");

                if (result == null)
                {
                    _logger.LogWarning("BAQ SO_SchdUpdate 查询无结果: PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum}",
                        poNum, orderLine, orderRelNum);
                }

                return (result, null);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("BAQ SO_SchdUpdate 调用失败：会话已过期 PONum={PONum}", poNum);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueryByJobAndPartAsync(PONum) 异常: PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum}",
                    poNum, orderLine, orderRelNum);
                return (null, "发货计划查询失败，请稍后重试");
            }
        }

    }
}
