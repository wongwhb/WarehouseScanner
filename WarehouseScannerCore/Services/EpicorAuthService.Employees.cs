using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarehouseScanner.Services
{
    /// <summary>业务方法 - 员工相关（仅 AccountController 登录链路使用）</summary>
    public partial class EpicorAuthService
    {
        /// <summary>根据员工ID获取基本信息（AccountController 登录时用于解析员工档案）</summary>
        public async Task<(string character02, string shift, string shortChar01, string firstName, string lastName)>
            EmpBasicGetByID(string EmpID, CancellationToken cancellationToken = default)
        {
            try
            {
                string url = $"Erp.BO.EmpBasicSvc/GetByID?empID={Uri.EscapeDataString(EmpID)}";
                string body = await GetBoJsonAsync(url, cancellationToken);
                return string.IsNullOrEmpty(body) ? ("error", "", "", "", "") : ParseEmpBasicResponse(body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取员工信息异常 EmpID:{EmpId}", EmpID);
                return ("error", "", "", "", "");
            }
        }
    }
}
