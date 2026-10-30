using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>
    /// Epicor 工单物料退回服务
    /// 职责：封装退回流程全部步骤（初始化 → 工序 → 数量 → 校验 → 执行）
    /// 错误处理：不抛异常，通过 HasApiError / LastApiError 暴露底层错误
    /// </summary>
    public class EpicorReturnService
    {
        private readonly EpicorAuthService _authService;
        private readonly string _company;
        private readonly ILogger<EpicorReturnService>? _logger;

        /// <summary>底层 EpicorAuthService 是否有 API 错误</summary>
        public bool HasApiError => _authService.HasApiError;

        /// <summary>最近一次 Epicor API 错误信息</summary>
        public string LastApiError => _authService.LastApiError;

        public EpicorReturnService(EpicorAuthService authService, string company, ILogger<EpicorReturnService>? logger = null)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _company = company ?? throw new ArgumentNullException(nameof(company));
            _logger = logger;
        }

        #region 核心方法：执行完整退回

        /// <summary>
        /// 执行完整的物料退回操作
        /// </summary>
        /// <param name="jobNum">工单号</param>
        /// <param name="assemblySeq">半成品装配序号（通常为0）</param>
        /// <param name="partNum">成品代码</param>
        /// <param name="MatNum">物料代码</param>
        /// <param name="whseCode">仓库代码</param>
        /// <param name="binNum">库位</param>
        /// <param name="lotNumber">批次号</param>
        /// <param name="quantity">退回数量</param>
        /// <param name="toJobSeq">目标工序号（可选）</param>
        /// <param name="tranType">事务类型，默认为 "MTL-STK"</param>
        /// <param name="callProcess">调用流程，默认为 "ReturnMaterial"</param>
        /// <returns>最终退回结果 JObject，失败返回 null，可通过 HasApiError 获取原因</returns>
        public async Task<JObject?> ReturnMaterialAsync(
            string jobNum,
            int assemblySeq,
            string partNum,
            string MatNum,
            string whseCode,
            string binNum,
            string lotNumber,
            double quantity,
            int? toJobSeq = null,
            string tranType = "MTL-STK",
            string callProcess = "ReturnMaterial")
        {
            #region 参数校验
            if (string.IsNullOrWhiteSpace(jobNum)) throw new ArgumentException("工单号不能为空", nameof(jobNum));
            if (string.IsNullOrWhiteSpace(MatNum)) throw new ArgumentException("物料代码不能为空", nameof(MatNum));
            if (quantity <= 0) throw new ArgumentException("数量必须大于0", nameof(quantity));
            binNum ??= string.Empty;
            lotNumber ??= string.Empty;
            #endregion

            try
            {
                #region 步骤1：初始化数据集
                var initResponse = await GetNewJobAsmblMultipleAsync(jobNum, assemblySeq, partNum, MatNum, null, tranType, callProcess);
                if (HasApiError || initResponse == null) return null;

                var issueReturnArray = initResponse["returnObj"]?["IssueReturn"] as JArray;
                if (issueReturnArray == null || issueReturnArray.Count == 0) return null;

                // 按物料定位目标行：GetNewJobAsmblMultiple 不带物料过滤，会返回该工单/装配下的全部物料行。
                // 必须按物料行定位：固定取首行且把全部行都置 RowMod="U" 会把库位/批次/数量
                // 写到别的物料行（RowMod="U" 为覆盖既有行），肉眼与日志都极难发现。
                // 说明：BO 请求体未加入 MatNum——仓库内没有证据表明该 BO 接受此列，贸然新增可能导致整单 400。
                bool IsTargetRow(JObject row)
                    => string.Equals((row.Value<string>("PartNum") ?? string.Empty).Trim(),
                                     MatNum.Trim(), StringComparison.OrdinalIgnoreCase);

                // 回退告警只打一次：本方法在「设置工序」与「设置库位」两处都会定位目标行
                bool fallbackWarned = false;

                // 目标行：① PartNum 与扫描物料一致；② 同一物料多行时按 FromJobSeq 收敛；
                // ③ PartNum 未命中时改用 FromJobSeq（调用方传入的 toJobSeq）收敛；
                // ④ 仍未命中则回退首行并发告警（不阻断退料）。
                List<JObject> ResolveTargetRows(JObject? currentDs)
                {
                    var rows = (currentDs?["IssueReturn"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                    var matched = rows.Where(IsTargetRow).ToList();
                    if (matched.Count > 1 && toJobSeq.HasValue)
                    {
                        var bySeq = matched.Where(r => r.Value<int?>("FromJobSeq") == toJobSeq.Value).ToList();
                        if (bySeq.Count > 0) matched = bySeq;
                    }
                    if (matched.Count > 0) return new List<JObject> { matched[0] };

                    if (toJobSeq.HasValue)
                    {
                        var bySeq = rows.Where(r => r.Value<int?>("FromJobSeq") == toJobSeq.Value).ToList();
                        if (bySeq.Count > 0) return new List<JObject> { bySeq[0] };
                    }

                    // 未命中则不阻断（避免因 ERP 字段语义差异导致全线退料失败），但必须留痕：
                    // 日志中的 PartNum 列表用于现场核对 IssueReturn 行究竟用哪个字段表示物料。
                    if (!fallbackWarned)
                    {
                        fallbackWarned = true;
                        _logger?.LogWarning(
                            "未能在 IssueReturn 中定位物料 {MatNum}（工序 {ToJobSeq}），已回退首行退料。工单 {JobNum}/{AssemblySeq}，返回行 PartNum：{Parts}",
                            MatNum, toJobSeq?.ToString() ?? "-", jobNum, assemblySeq,
                            string.Join("|", rows.Select(r => r.Value<string>("PartNum"))));
                    }
                    return rows.Take(1).ToList();
                }

                var ds = new JObject { ["IssueReturn"] = issueReturnArray.DeepClone() };

                // 工序号只作用于目标行：若把全部物料行都改成目标工序并置 RowMod="U"，
                // 等于让其它物料行一起参与后续过账。
                int currentFromJobSeq = ResolveTargetRows(ds).FirstOrDefault()?.Value<int?>("FromJobSeq") ?? 0;

                if (toJobSeq.HasValue && toJobSeq.Value != currentFromJobSeq)
                {
                    foreach (var item in ResolveTargetRows(ds))
                    {
                        item["FromJobSeq"] = toJobSeq.Value;
                        item["RowMod"] = "U";
                    }
                }
                #endregion

                #region 步骤2：变更工序序列
                int effectiveJobSeq = toJobSeq.GetValueOrDefault(currentFromJobSeq);
                var changingJobSeq = await OnChangingJobSeqAsync(ds, effectiveJobSeq.ToString(), "From", "ReturnMaterial");
                if (HasApiError || changingJobSeq == null) return null;
                ds = EpicorJsonHelper.ExtractDs(changingJobSeq);

                if (toJobSeq.HasValue && toJobSeq.Value != currentFromJobSeq)
                {
                    var afterJobSeqResponse = await OnChangeFromJobSeqAsync(ds, toJobSeq.Value, callProcess);
                    if (HasApiError || afterJobSeqResponse == null) return null;
                    ds = EpicorJsonHelper.ExtractDs(afterJobSeqResponse);
                }
                #endregion

                #region 步骤3：设置库位信息
                // 只改目标物料行
                var issueReturn = ResolveTargetRows(ds).FirstOrDefault();
                if (issueReturn != null)
                {
                    issueReturn["FromBinNum"] = binNum;
                    issueReturn["FromWarehouseCode"] = whseCode;
                    issueReturn["LotNum"] = lotNumber;
                    issueReturn["TranType"] = string.IsNullOrWhiteSpace(tranType) ? "MTL-STK" : tranType;
                    issueReturn["RowMod"] = "U";
                }
                #endregion

                #region 步骤4：设置退回数量
                var afterQtyResponse = await OnChangeTranQtyAsync(ds!, quantity);
                if (HasApiError || afterQtyResponse == null) return null;
                ds = EpicorJsonHelper.ExtractDs(afterQtyResponse);
                #endregion

                #region 步骤5：物料移动前校验
                var prePerformResponse = await PrePerformMaterialMovementAsync(ds);
                if (HasApiError || prePerformResponse == null) return null;
                ds = EpicorJsonHelper.ExtractDs(prePerformResponse);
                #endregion

                #region 步骤6：库存库位校验
                var masterTestsResponse = await MasterInventoryBinTestsAsync(ds);
                if (HasApiError || masterTestsResponse == null) return null;
                ds = EpicorJsonHelper.ExtractDs(masterTestsResponse);
                #endregion

                #region 步骤7：执行物料移动（PerformMaterialMovement2）
                var finalResponse = await PerformMaterialMovement2Async(ds, plNegQtyAction: false);
                if (HasApiError || finalResponse == null) return null;
                return finalResponse;
                #endregion
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "退回操作异常 Company:{Company}", _company);
                _authService.SetApiError("物料退回处理失败，请稍后重试");
                return null;
            }
        }

        #endregion

        #region API 步骤方法

        /// <summary>步骤1：初始化退回数据集</summary>
        public async Task<JObject> GetNewJobAsmblMultipleAsync(
            string jobNum, int assemblySeq, string partNum, string MatNum,
            string? description = null, string tranType = "MTL-STK", string callProcess = "ReturnMaterial")
        {
            var requestBody = new
            {
                pcTranType = tranType,
                pcMtlQueueRowID = "00000000-0000-0000-0000-000000000000",
                pCallProcess = callProcess,
                ds = new
                {
                    SelectedJobAsmbl = new[]
                    {
                        new
                        {
                            Company = _company,
                            JobNum = jobNum,
                            AssemblySeq = assemblySeq,
                            PartNum = partNum,
                            Description = description ?? string.Empty,
                            JobHeadPartNum = partNum,
                            JobHeadPartDescription = description ?? string.Empty,
                            EquipID = string.Empty,
                            SysRowID = "00000000-0000-0000-0000-000000000000",
                            RowMod = "A"
                        }
                    }
                }
            };

            string jsonPayload = EpicorJsonHelper.Serialize(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.IssueReturnSvc/GetNewJobAsmblMultiple", jsonPayload);
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        /// <summary>步骤2：变更工序序列</summary>
        public async Task<JObject> OnChangingJobSeqAsync(JObject ds, string piJobSeq, string pcDirection, string pCallProcess)
        {
            var requestObj = new JObject
            {
                ["piJobSeq"] = piJobSeq,
                ["pcDirection"] = pcDirection,
                ["pCallProcess"] = pCallProcess,
                ["ds"] = ds
            };

            string jsonPayload = requestObj.ToString(Formatting.None);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.IssueReturnSvc/OnChangingJobSeq", jsonPayload);
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        /// <summary>步骤3：变更源工序序列（退回专用）</summary>
        public async Task<JObject> OnChangeFromJobSeqAsync(
            JObject ds, int jobSeq, string direction = "From", string callProcess = "ReturnMaterial")
        {
            var requestBody = new { pCallProcess = callProcess, ds = ds };
            string jsonPayload = EpicorJsonHelper.Serialize(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.IssueReturnSvc/OnChangeFromJobSeq", jsonPayload);
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        /// <summary>步骤4：设置退回数量</summary>
        public async Task<JObject> OnChangeTranQtyAsync(JObject ds, double tranQty)
        {
            var requestObj = new JObject
            {
                // 数量转字符串必须用 InvariantCulture，避免区域文化把小数点写成逗号
                ["pdTranQty"] = tranQty.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ds"] = ds
            };

            string jsonPayload = requestObj.ToString(Formatting.None);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.IssueReturnSvc/OnChangeTranQty", jsonPayload);
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        /// <summary>步骤5：物料移动前校验</summary>
        public async Task<JObject> PrePerformMaterialMovementAsync(JObject ds)
        {
            var requestObj = new JObject { ["ds"] = ds };
            string jsonPayload = requestObj.ToString(Formatting.None);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.IssueReturnSvc/PrePerformMaterialMovement", jsonPayload);
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        /// <summary>步骤6：库存库位校验</summary>
        public async Task<JObject> MasterInventoryBinTestsAsync(JObject ds)
        {
            var requestObj = new JObject { ["ds"] = ds };
            string jsonPayload = requestObj.ToString(Formatting.None);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.IssueReturnSvc/MasterInventoryBinTests", jsonPayload);
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        /// <summary>步骤7：执行物料移动（退回使用 PerformMaterialMovement2）</summary>
        public async Task<JObject> PerformMaterialMovement2Async(JObject ds, bool plNegQtyAction = false)
        {
            var requestBody = new { plNegQtyAction = plNegQtyAction, ds = ds };
            string jsonPayload = EpicorJsonHelper.Serialize(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.IssueReturnSvc/PerformMaterialMovement2", jsonPayload);
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        #endregion
    }
}