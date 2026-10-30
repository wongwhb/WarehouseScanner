using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>
    /// Epicor 工单入库（制造收货）服务类
    /// 实现 ReceiptsFromMfgSvc 相关的 API 调用
    /// </summary>
    public partial class EpicorReceiptService
    {
        private readonly EpicorAuthService _authService;
        private readonly string _company;
        private readonly string _plant;
        private readonly ILogger<EpicorReceiptService>? _logger;

        private readonly string _operatorId;

        public EpicorReceiptService(EpicorAuthService authService, string company, string operatorId = "", ILogger<EpicorReceiptService>? logger = null, string plant = "")
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _company = company ?? throw new ArgumentNullException(nameof(company));
            _operatorId = operatorId ?? string.Empty;
            _logger = logger;
            _plant = plant ?? string.Empty;
        }

        #region 错误状态传递
        public bool HasApiError => _authService.HasApiError;
        public string LastApiError => _authService.LastApiError;
        #endregion

        #region 完整入库流程

        /// <summary>
        /// 执行完整的制造入库操作（单阶段，业务允许超收）
        /// 按顺序调用 6 个 API 步骤完成工单入库。
        /// pcMessage（超收警告等）仅作为附加信息返回，不阻断入库流程。
        /// </summary>
        /// <returns>(result, warningMessage) — result 为 null 表示失败（检查 HasApiError）；warningMessage 为 Epicor BO 警告文案（可能为空）</returns>
        public async Task<(JObject? result, string? warningMessage)> ExecuteReceiptAsync(
            string jobNum, int assemblySeq, string partNum,
            string whseCode, string binNum, string lotNum,
            decimal quantity, string processId = "JobReceiptToInventory",
            string tranType = "MFG-STK")
        {
            if (string.IsNullOrEmpty(jobNum))
                throw new ArgumentException("工单号不能为空", nameof(jobNum));

            double qty = (double)quantity;
            string? warningMessage = null;

            try
            {
                // 步骤1: 获取新的收货数据集
                var initResponse = await GetNewReceiptsFromMfgJobAsmAsync(
                    jobNum, assemblySeq, tranType, processId);
                if (EpicorJsonHelper.HasError(initResponse))
                {
                    _authService.SetApiError(EpicorJsonHelper.GetErrorMessage(initResponse));
                    return (null, null);
                }

                var ds = EpicorJsonHelper.ExtractDs(initResponse);
                if (ds == null)
                {
                    _authService.SetApiError("初始化收货数据集失败：响应中无数据集");
                    return (null, null);
                }

                // 设置 PartTran 数据（含数量字段）
                ds = SetPartTranData(ds, jobNum, assemblySeq, partNum, whseCode, binNum, lotNum, qty);

                // 步骤2: OnChangeActTranQty - 触发 Epicor BO 数量校验，获取 pcMessage（仅作提示，不阻断）
                var onChangeQtyResponse = await OnChangeActTranQtyAsync(ds);
                if (EpicorJsonHelper.HasError(onChangeQtyResponse))
                {
                    _authService.SetApiError($"步骤2 OnChangeActTranQty 失败: {EpicorJsonHelper.GetErrorMessage(onChangeQtyResponse)}");
                    return (null, null);
                }

                // 提取 pcMessage 作为警告文案（超收提示等），业务允许超收，不中断流程
                warningMessage = onChangeQtyResponse?["parameters"]?["pcMessage"]?.ToString();
                if (!string.IsNullOrWhiteSpace(warningMessage))
                {
                    _logger?.LogWarning("步骤2 OnChangeActTranQty pcMessage: {PcMessage}, JobNum={JobNum}, AssemblySeq={AssemblySeq}, Qty={Qty}",
                        warningMessage, jobNum, assemblySeq, qty);
                }

                ds = EpicorJsonHelper.ExtractDs(onChangeQtyResponse);
                if (ds == null)
                {
                    _authService.SetApiError("步骤2 OnChangeActTranQty 返回数据集为空");
                    return (null, null);
                }

                // 步骤3: ChkForNeedsLotAttrs - 检查批次属性需求
                if (!string.IsNullOrEmpty(lotNum))
                {
                    var chkForNeedsResponse = await ChkForNeedsLotAttrsAsync(partNum, lotNum, "None");
                    if (EpicorJsonHelper.HasError(chkForNeedsResponse))
                    {
                        _authService.SetApiError($"步骤3 ChkForNeedsLotAttrs 失败: {EpicorJsonHelper.GetErrorMessage(chkForNeedsResponse)}");
                        return (null, null);
                    }
                }

                // 步骤4: PreUpdate - 更新数据集
                var preUpdateResponse = await PreUpdateAsync(ds, jobNum, lotNum, qty, whseCode);
                if (EpicorJsonHelper.HasError(preUpdateResponse))
                {
                    _authService.SetApiError($"步骤4 PreUpdate 失败: {EpicorJsonHelper.GetErrorMessage(preUpdateResponse)}");
                    return (null, null);
                }
                ds = EpicorJsonHelper.ExtractDs(preUpdateResponse);
                if (ds == null)
                {
                    _authService.SetApiError("步骤4 PreUpdate 返回数据集为空");
                    return (null, null);
                }

                // 步骤5: VerifySerialMatchAndPlanContract
                var verifyResponse = await VerifySerialMatchAndPlanContractAsync(ds);
                if (EpicorJsonHelper.HasError(verifyResponse))
                {
                    _authService.SetApiError($"步骤5 VerifySerialMatchAndPlanContract 失败: {EpicorJsonHelper.GetErrorMessage(verifyResponse)}");
                    return (null, null);
                }

                // 步骤6: ReceiveMfgPartToInventory - 执行入库
                var finalResponse = await ReceiveMfgPartToInventoryAsync(
                    ds, serialNoQty: qty, negQtyAction: false, pcProcessID: processId);
                if (EpicorJsonHelper.HasError(finalResponse))
                {
                    _authService.SetApiError($"步骤6 ReceiveMfgPartToInventory 失败: {EpicorJsonHelper.GetErrorMessage(finalResponse)}");
                    return (null, null);
                }

                return (finalResponse, warningMessage);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "入库操作异常 Company:{Company}", _company);
                // 不透传 ex.Message（可能含内部主机名/URL/SQL 片段），仅写服务端日志
                _authService.SetApiError("入库处理失败，请稍后重试或联系管理员");
                throw;
            }
        }

        /// <summary>
        /// 兼容旧接口：执行完整的制造入库操作
        /// 内部调用 ExecuteReceiptAsync，pcMessage 仅记录日志，不返回给调用方。
        /// </summary>
        public async Task<JObject?> ExecuteCompleteReceiptAsync(
            string jobNum, int assemblySeq, string partNum,
            string whseCode, string binNum, string lotNum,
            decimal quantity, string processId = "JobReceiptToInventory",
            string tranType = "MFG-STK")
        {
            var (result, _) = await ExecuteReceiptAsync(
                jobNum, assemblySeq, partNum, whseCode, binNum, lotNum, quantity, processId, tranType);
            return result!;
        }

        #endregion
    }
}
