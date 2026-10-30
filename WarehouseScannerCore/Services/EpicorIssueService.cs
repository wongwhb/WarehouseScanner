using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>
    /// Epicor 工单发料服务类
    /// 依赖于 EpicorAuthService 获取有效 Token，提供完整的物料发放流程调用
    /// </summary>
    public class EpicorIssueService
    {
        private readonly EpicorAuthService _authService;
        private readonly string _company;
        private readonly ILogger<EpicorIssueService>? _logger;

        public EpicorIssueService(EpicorAuthService authService, string company, ILogger<EpicorIssueService>? logger = null)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _company = company ?? throw new ArgumentNullException(nameof(company));
            _logger = logger;
        }

        #region 错误状态传递（委托给 EpicorAuthService）
        public bool HasApiError => _authService.HasApiError;
        public string LastApiError => _authService.LastApiError;
        #endregion

        #region 完整发料流程

        /// <summary>
        /// 执行完整的物料发料操作
        /// </summary>
        public async Task<JObject> IssueMaterialAsync(
            string jobNum, int assemblySeq, string partNum, string MatNum,
            string whseCode, string binNum, string lotNumber, double quantity,
            int? toJobSeq = null, string tranType = "STK-MTL", string callProcess = "IssueMaterial")
        {
            if (string.IsNullOrEmpty(jobNum))
                throw new ArgumentException("工单号不能为空", nameof(jobNum));
            if (string.IsNullOrEmpty(MatNum))
                throw new ArgumentException("物料代码不能为空", nameof(MatNum));
            if (quantity <= 0)
                throw new ArgumentException("数量必须大于0", nameof(quantity));

            try
            {
                // 步骤1: 初始化数据集
                var initResponse = await GetNewJobAsmblMultipleAsync(jobNum, assemblySeq, partNum, MatNum, null, tranType, callProcess);
                if (EpicorJsonHelper.HasError(initResponse))
                    throw new InvalidOperationException(EpicorJsonHelper.GetErrorMessage(initResponse));

                var issueReturnArray = initResponse["returnObj"]?["IssueReturn"] as JArray;
                if (issueReturnArray == null || issueReturnArray.Count == 0)
                    throw new InvalidOperationException("未找到物料发料信息");

                // 按物料定位目标行：GetNewJobAsmblMultiple 不带物料过滤，会返回该工单/装配下的全部物料行。
                // 必须按物料行定位而非固定取首行：多物料工单下固定取 [0] 会把库位/批次/数量
                // 写到另一个物料行（RowMod="U" 为覆盖），肉眼难发现。
                // 说明：BO 请求体未加入 MatNum——仓库内没有证据表明该 BO 接受此列，贸然新增可能导致整单 400。
                bool IsTargetRow(JObject row)
                    => string.Equals((row.Value<string>("PartNum") ?? string.Empty).Trim(),
                                     MatNum.Trim(), StringComparison.OrdinalIgnoreCase);

                // 回退告警只打一次：本方法在「设置工序」与「设置库位」两处都会定位目标行
                bool fallbackWarned = false;

                // 目标行：① PartNum 与扫描物料一致；② 同一物料多行时按 ToJobSeq 收敛；
                // ③ PartNum 未命中时改用 ToJobSeq（JobMtl.MtlSeq，调用方传入）收敛；
                // ④ 仍未命中则回退首行并发告警（不阻断发料）。
                List<JObject> ResolveTargetRows(JObject? currentDs)
                {
                    var rows = (currentDs?["IssueReturn"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                    var matched = rows.Where(IsTargetRow).ToList();
                    if (matched.Count > 1 && toJobSeq.HasValue)
                    {
                        var bySeq = matched.Where(r => r.Value<int?>("ToJobSeq") == toJobSeq.Value).ToList();
                        if (bySeq.Count > 0) matched = bySeq;
                    }
                    if (matched.Count > 0) return new List<JObject> { matched[0] };

                    if (toJobSeq.HasValue)
                    {
                        var bySeq = rows.Where(r => r.Value<int?>("ToJobSeq") == toJobSeq.Value).ToList();
                        if (bySeq.Count > 0) return new List<JObject> { bySeq[0] };
                    }

                    // 未命中则不阻断（避免因 ERP 字段语义差异导致全线发料失败），但必须留痕：
                    // 日志中的 PartNum 列表用于现场核对 IssueReturn 行究竟用哪个字段表示物料。
                    if (!fallbackWarned)
                    {
                        fallbackWarned = true;
                        _logger?.LogWarning(
                            "未能在 IssueReturn 中定位物料 {MatNum}（物料序号 {ToJobSeq}），已回退首行发料。工单 {JobNum}/{AssemblySeq}，返回行 PartNum：{Parts}",
                            MatNum, toJobSeq?.ToString() ?? "-", jobNum, assemblySeq,
                            string.Join("|", rows.Select(r => r.Value<string>("PartNum"))));
                    }
                    return rows.Take(1).ToList();
                }

                var ds = new JObject { ["IssueReturn"] = issueReturnArray.DeepClone() };

                // 设置工序号（如果提供）——仅作用于目标行，避免把其它物料行一并改成 RowMod="U"
                if (toJobSeq.HasValue)
                {
                    foreach (var item in ResolveTargetRows(ds))
                    {
                        item["ToJobSeq"] = toJobSeq.Value;
                        item["RowMod"] = "U";
                    }
                }

                // 步骤2: OnChangeToJobSeq - 校验并更新工序相关字段
                if (toJobSeq.HasValue)
                {
                    var afterJobSeqResponse = await OnChangeToJobSeqAsync(ds, callProcess);
                    if (EpicorJsonHelper.HasError(afterJobSeqResponse))
                        throw new InvalidOperationException("设置工序失败");
                    ds = EpicorJsonHelper.ExtractDs(afterJobSeqResponse);
                }

                // 步骤3: OnChangeFromBinNum - 设置库位（只改目标物料行）
                var issueReturn = ResolveTargetRows(ds).FirstOrDefault();
                if (issueReturn != null)
                {
                    issueReturn["FromBinNum"] = binNum;
                    issueReturn["FromWarehouseCode"] = whseCode;
                    issueReturn["LotNum"] = lotNumber;
                    issueReturn["RowMod"] = "U";
                }

                var binResponse = await OnChangeFromBinNumAsync(ds!);
                if (EpicorJsonHelper.HasError(binResponse))
                    throw new InvalidOperationException("设置 FromBinNum 失败");
                ds = EpicorJsonHelper.ExtractDs(binResponse);

                // 步骤4: OnChangeTranQty - 设置发料数量
                var afterQtyResponse = await OnChangeTranQtyAsync(ds, quantity);
                if (EpicorJsonHelper.HasError(afterQtyResponse))
                    throw new InvalidOperationException("设置数量失败");
                ds = EpicorJsonHelper.ExtractDs(afterQtyResponse);

                // 步骤5: PrePerformMaterialMovement - 最终校验
                var prePerformResponse = await PrePerformMaterialMovementAsync(ds);
                if (EpicorJsonHelper.HasError(prePerformResponse))
                    throw new InvalidOperationException("物料移动前校验失败");
                ds = EpicorJsonHelper.ExtractDs(prePerformResponse);

                // 步骤6: PerformMaterialMovement - 执行发料
                var finalResponse = await PerformMaterialMovementAsync(ds, plNegQtyAction: false);
                if (EpicorJsonHelper.HasError(finalResponse))
                    throw new InvalidOperationException(EpicorJsonHelper.GetErrorMessage(finalResponse));

                return finalResponse;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "发料操作异常 Company:{Company}", _company);
                throw;
            }
        }

        #endregion

        #region API 步骤方法

        public async Task<JObject> GetNewJobAsmblMultipleAsync(
            string jobNum, int assemblySeq, string partNum, string MatNum,
            string? description = null, string tranType = "STK-MTL", string callProcess = "IssueMaterial")
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
                            Company = _company, JobNum = jobNum, AssemblySeq = assemblySeq,
                            PartNum = partNum, Description = description ?? "",
                            JobHeadPartNum = partNum, JobHeadPartDescription = description ?? "",
                            EquipID = "", SysRowID = "00000000-0000-0000-0000-000000000000", RowMod = "A"
                        }
                    }
                }
            };

            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.IssueReturnSvc/GetNewJobAsmblMultiple", EpicorJsonHelper.Serialize(requestBody));
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        public async Task<JObject> OnChangeToJobSeqAsync(JObject ds, string callProcess = "IssueMaterial")
        {
            var requestBody = new { pCallProcess = callProcess, ds };
            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.IssueReturnSvc/OnChangeToJobSeq", EpicorJsonHelper.Serialize(requestBody));
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        public async Task<JObject> OnChangeTranQtyAsync(JObject ds, double tranQty)
        {
            var requestObj = new JObject
            {
                ["pdTranQty"] = tranQty.ToString(CultureInfo.InvariantCulture),
                ["ds"] = ds
            };
            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.IssueReturnSvc/OnChangeTranQty", requestObj.ToString(Formatting.None));
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        public async Task<JObject> OnChangeFromBinNumAsync(JObject ds)
        {
            var requestBody = new { ds };
            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.IssueReturnSvc/OnChangeFromBinNum", EpicorJsonHelper.Serialize(requestBody));
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        public async Task<JObject> PrePerformMaterialMovementAsync(JObject ds)
        {
            var requestObj = new JObject { ["ds"] = ds };
            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.IssueReturnSvc/PrePerformMaterialMovement", requestObj.ToString(Formatting.None));
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        public async Task<JObject> PerformMaterialMovementAsync(JObject ds, bool plNegQtyAction = false)
        {
            var requestBody = new { plNegQtyAction, ds };
            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.IssueReturnSvc/PerformMaterialMovement", EpicorJsonHelper.Serialize(requestBody));
            return string.IsNullOrEmpty(responseJson) ? null! : JObject.Parse(responseJson);
        }

        #endregion

        #region 发料信息查询

        public async Task<List<IssueReturnItem>?> GetMaterialIssueInfoAsync(
            string jobNum, int assemblySeq, string? partNum = null, string? MatNum = null)
        {
            var response = await GetNewJobAsmblMultipleAsync(jobNum, assemblySeq, partNum ?? "", MatNum ?? "");
            if (response == null) return null;

            var issueReturnArray = response["returnObj"]?["IssueReturn"] as JArray;
            if (issueReturnArray == null) return new List<IssueReturnItem>();

            var items = issueReturnArray.ToObject<List<IssueReturnItem>>()!;
            if (!string.IsNullOrEmpty(partNum))
                items = items.Where(i => i.PartNum?.Equals(partNum, StringComparison.OrdinalIgnoreCase) == true).ToList();
            return items;
        }

        #endregion
    }

    #region 数据模型类

    public class IssueReturnItem
    {
        [JsonProperty("Company")] public string Company { get; set; } = null!;
        [JsonProperty("PartNum")] public string PartNum { get; set; } = null!;
        [JsonProperty("TranQty")] public decimal TranQty { get; set; }
        [JsonProperty("IUM")] public string IUM { get; set; } = null!;
        [JsonProperty("UM")] public string UM { get; set; } = null!;
        [JsonProperty("ToJobNum")] public string ToJobNum { get; set; } = null!;
        [JsonProperty("ToAssemblySeq")] public int ToAssemblySeq { get; set; }
        [JsonProperty("ToJobSeq")] public int ToJobSeq { get; set; }
        [JsonProperty("QtyRequired")] public decimal QtyRequired { get; set; }
        [JsonProperty("QtyPreviouslyIssued")] public decimal QtyPreviouslyIssued { get; set; }
        [JsonProperty("OnHandQty")] public decimal OnHandQty { get; set; }
        [JsonProperty("Plant")] public string Plant { get; set; } = null!;
        [JsonProperty("TranType")] public string TranType { get; set; } = null!;
        [JsonProperty("DimConvFactor")] public decimal DimConvFactor { get; set; }
        [JsonProperty("ProcessID")] public string ProcessID { get; set; } = null!;
        [JsonProperty("SysRowID")] public string SysRowID { get; set; } = null!;
        [JsonProperty("ToJobPartNum")] public string ToJobPartNum { get; set; } = null!;
        [JsonProperty("ToAssemblyPartNum")] public string ToAssemblyPartNum { get; set; } = null!;
        [JsonProperty("ToJobPartDescription")] public string ToJobPartDescription { get; set; } = null!;
        [JsonProperty("ToAssemblyPartDescription")] public string ToAssemblyPartDescription { get; set; } = null!;
    }

    #endregion
}
