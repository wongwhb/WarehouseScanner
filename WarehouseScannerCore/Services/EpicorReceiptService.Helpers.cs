using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>辅助方法 / 仓库与库位存在性验证</summary>
    public partial class EpicorReceiptService
    {
        #region 辅助方法

        /// <summary>
        /// 设置 PartTran 中的基础数据
        /// </summary>
        private JObject SetPartTranData(JObject ds, string jobNum, int assemblySeq,
            string partNum, string whseCode, string binNum, string lotNum, double quantity)
        {
            if (ds?["PartTran"] is JArray partTranArray && partTranArray.Count > 0)
            {
                foreach (var item in partTranArray.OfType<JObject>())
                {
                    item["JobNum"] = jobNum;
                    item["AssemblySeq"] = assemblySeq;
                    item["PartNum"] = partNum ?? string.Empty;
                    item["WareHouseCode"] = whseCode ?? string.Empty;
                    item["BinNum"] = binNum ?? string.Empty;
                    item["LotNum"] = lotNum ?? jobNum;
                    item["TranQty"] = quantity;
                    item["ActTranQty"] = quantity;
                    item["ThisTranQty"] = quantity;
                    item["RowMod"] = "U";

                    // 设置公司
                    if (!string.IsNullOrEmpty(_company))
                        item["Company"] = _company;
                }
            }

            // 更新 JobReceiptDemand
            if (ds?["JobReceiptDemand"] is JArray demandArray && demandArray.Count > 0)
            {
                foreach (var item in demandArray.OfType<JObject>())
                {
                    item["JobNum"] = jobNum;
                    item["PartNum"] = partNum ?? string.Empty;
                    item["RowMod"] = "U";
                }
            }

            return ds!;
        }

        /// <summary>
        /// 获取物料的默认仓库 + 单位（PartSvc/PartPlants API）
        /// </summary>
        /// <returns>
        /// (warehouseCode, unit, partNotFound)：
        /// - partNotFound=true：物料/物料-工厂记录在系统中不存在（含 404 Entity not found、查无数据、仓库与单位均为空）
        /// - partNotFound=false：查询成功
        /// - 返回 null：物料编码格式非法或其他接口故障（超时/认证/服务端错误），调用方可通过 HasApiError 区分
        /// </returns>
        public async Task<(string warehouseCode, string unit, bool partNotFound)?> GetPartDefaultWarehouseAsync(string partNum, string? company = null, string? plant = null)
        {
            company = company ?? _company;
            plant = plant ?? _plant;
            // 站点未配置工厂时无法按 PartPlant 键查询（PartPlants 键为 公司+工厂+物料）。
            // 不能回退到任何具体工厂字面值：那会让未配置 Plant 的站点拿别的工厂去查，
            // 得到 404 后被误判为「物料不存在」而阻断合法收发料。
            // 此处返回 partNotFound=false、仓库/单位为空，由调用方提示「需人工选择仓库」。
            if (string.IsNullOrEmpty(plant))
            {
                _logger?.LogWarning("站点未配置工厂，跳过 PartPlant 默认仓库查询 PartNum:{PartNum}", partNum);
                return (string.Empty, string.Empty, false);
            }
            try
            {
                // 安全：物料编码位于 OData 键路径中（单引号包裹），必须白名单校验，防止键路径注入
                if (!ODataFilterBuilder.IsValidPartNum(partNum))
                {
                    _logger?.LogWarning("物料编码格式非法，跳过默认仓库查询 PartNum:{PartNum}", partNum);
                    return null;
                }

                // OData 键路径必须双重处理：先把单引号转义为 OData 规范的 ''，再做 URL 编码，
                // 否则含单引号的物料编码可闭合键路径字面量，形成 OData 注入。
                string url = $"Erp.BO.PartSvc/PartPlants('{Uri.EscapeDataString(ODataFilterBuilder.EscapeString(company))}','{Uri.EscapeDataString(ODataFilterBuilder.EscapeString(plant))}','{Uri.EscapeDataString(ODataFilterBuilder.EscapeString(partNum))}')";

                string responseJson = await _authService.GetODataJsonAsync(url);

                // 404 Entity not found（如 "[404] Entity PartPlant with specified keys is not found"）
                // 属业务结果——该物料在系统中不存在，而非接口故障：
                // 转换为 partNotFound=true 返回，并清除错误状态，避免污染调用方后续的 HasApiError 判断
                if (_authService.HasApiError)
                {
                    string apiError = _authService.LastApiError ?? string.Empty;
                    bool entityNotFound =
                        apiError.StartsWith("[404]", StringComparison.Ordinal) ||
                        apiError.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (entityNotFound)
                    {
                        _logger?.LogInformation("PartPlants 查询确认物料不存在: PartNum={PartNum}", partNum);
                        _authService.ClearApiError();
                        return (null!, null!, true);
                    }

                    // 其他接口错误（超时/认证/服务端错误等）→ 返回 null，由调用方按接口故障处理
                    return null;
                }

                if (string.IsNullOrEmpty(responseJson))
                    return (null!, null!, true);

                var jObj = JObject.Parse(responseJson);

                string? primWhse = jObj["PrimWhse"]?.ToString();
                string? partIUM = jObj["PartIUM"]?.ToString();

                if (string.IsNullOrEmpty(primWhse))
                {
                    var values = jObj["value"] as JArray;
                    if (values != null && values.Any())
                    {
                        var first = values.First() as JObject;
                        primWhse = first?["PrimWhse"]?.ToString();
                        if (string.IsNullOrEmpty(primWhse))
                            primWhse = first?["WarehouseCode"]?.ToString() ?? first?["WhseCode"]?.ToString();
                        partIUM = partIUM ?? first?["PartIUM"]?.ToString();
                    }
                }

                if (string.IsNullOrEmpty(primWhse))
                {
                    primWhse = jObj["WarehouseCode"]?.ToString()
                        ?? jObj["WhseCode"]?.ToString()
                        ?? jObj["PartPlant_WarehouseCode"]?.ToString();
                }

                // 仓库和单位都为空 → 物料在系统中不存在
                if (string.IsNullOrEmpty(primWhse) && string.IsNullOrEmpty(partIUM))
                {
                    return (null!, null!, true);
                }

                return (primWhse!, partIUM!, false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetPartDefaultWarehouse 异常: PartNum={PartNum}", partNum);
                _authService.SetApiError("物料默认仓库查询失败，请稍后重试");
                return null;
            }
        }

        /// <summary>
        /// 通过 ReceiptsFromMfgSvc/GetRows 查询工单装配序号
        /// </summary>
        /// <returns>AssemblySeq，查不到或出错时返回 null</returns>
        public async Task<int?> GetAssemblySeqFromMfgAsync(string jobNum, string partNum)
        {
            if (string.IsNullOrEmpty(jobNum) || string.IsNullOrEmpty(partNum))
                return null;

            try
            {
                // 安全：工单号/物料编码白名单校验 + OData 单引号转义，防止 OData 注入
                if (!ODataFilterBuilder.IsValidJobNum(jobNum) || !ODataFilterBuilder.IsValidPartNum(partNum))
                {
                    _logger?.LogWarning("工单号或物料编码格式非法，跳过装配序号查询 JobNum:{JobNum}, PartNum:{PartNum}",
                        jobNum, partNum);
                    return null;
                }

                string where = $"JobNum='{ODataFilterBuilder.EscapeString(jobNum)}' and PartNum='{ODataFilterBuilder.EscapeString(partNum)}'";
                string url = $"Erp.BO.ReceiptsFromMfgSvc/GetRows?whereClauseJobHead=&whereClauseJobAsmbl={Uri.EscapeDataString(where)}&pageSize=20&absolutePage=1";

                string responseJson = await _authService.GetBoJsonAsync(url);
                if (string.IsNullOrEmpty(responseJson)) return null;

                var jObj = JObject.Parse(responseJson);

                var asmblArray = jObj["returnObj"]?["ReceiptsFromMfgJobAsmbl"] as JArray;
                if (asmblArray != null && asmblArray.Any())
                {
                    var first = asmblArray.First() as JObject;
                    if (first?["AssemblySeq"] != null && int.TryParse(first!["AssemblySeq"]!.ToString(), out var seq))
                        return seq;
                }

                var values = jObj["value"] as JArray;
                if (values != null && values.Any())
                {
                    var first = values.First() as JObject;
                    if (first?["AssemblySeq"] != null && int.TryParse(first!["AssemblySeq"]!.ToString(), out var seq2))
                        return seq2;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetAssemblySeqFromMfgAsync 异常: JobNum={JobNum}, PartNum={PartNum}", jobNum, partNum);
                _authService.SetApiError("装配序号查询失败，请稍后重试");
                return null;
            }
        }

        /// <summary>
        /// 获取仓库的默认库位（WhseBinSvc/GetRows API）
        /// </summary>
        public async Task<string> GetWhseBinDefaultAsync(string warehouseCode)
        {
            if (string.IsNullOrEmpty(warehouseCode))
                return null!;

            try
            {
                // 安全：仓库编码白名单校验 + OData 单引号转义，防止 OData 注入
                if (!ODataFilterBuilder.IsValidCode(warehouseCode))
                {
                    _logger?.LogWarning("仓库编码格式非法，跳过默认库位查询 WarehouseCode:{Whse}", warehouseCode);
                    return null!;
                }

                string url = $"Erp.BO.WhseBinSvc/GetRows?whereClauseWhseBin=WarehouseCode%3D%27{Uri.EscapeDataString(ODataFilterBuilder.EscapeString(warehouseCode))}%27&whereClauseWhseBinAttch=&pageSize=1&absolutePage=1";

                string responseJson = await _authService.GetBoJsonAsync(url);

                if (string.IsNullOrEmpty(responseJson))
                    return null!;

                var jObj = JObject.Parse(responseJson);

                var whseBinArray = jObj["returnObj"]?["WhseBin"] as JArray;
                if (whseBinArray != null && whseBinArray.Any())
                {
                    var first = whseBinArray.First() as JObject;
                    var binNum = first?["BinNum"]?.ToString().Trim();
                    if (!string.IsNullOrEmpty(binNum))
                        return binNum;
                }

                var values = jObj["value"] as JArray;
                if (values != null && values.Any())
                {
                    var first = values.First() as JObject;
                    var binNum = first?["BinNum"]?.ToString();
                    if (!string.IsNullOrEmpty(binNum))
                        return binNum;
                }

                return null!;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetWhseBinDefault 异常: WarehouseCode={WarehouseCode}", warehouseCode);
                _authService.SetApiError("默认库位查询失败，请稍后重试");
                return null!;
            }
        }

        #region 仓库与库位存在性验证

        /// <summary>
        /// 验证指定仓库是否存在（OData API: WarehseSvc/Warehses）
        /// </summary>
        public async Task<(bool exists, string warehouseCode)> CheckWarehouseExistsAsync(string warehouseCode)
        {
            if (string.IsNullOrEmpty(warehouseCode))
                return (false, warehouseCode);

            // 注意：此处不吞异常。API 调用异常（超时/认证失效等）应向上传播，
            // 由调用方区分"仓库不存在"与"接口异常"，避免误报"仓库不存在"。
            string filter = ODataFilterBuilder.Eq("WarehouseCode", warehouseCode);
            string url = ODataFilterBuilder.BuildUrl("Erp.BO.WarehseSvc/Warehses", filter, top: 1);

            string responseJson = await _authService.GetODataJsonAsync(url);

            if (string.IsNullOrEmpty(responseJson))
                return (false, warehouseCode);

            var jObj = JObject.Parse(responseJson);
            var values = jObj["value"] as JArray;
            bool exists = values != null && values.Any();

            return (exists, warehouseCode);
        }

        /// <summary>
        /// 验证指定库位是否存在（OData API: WhseBinSvc/WhseBins）
        /// </summary>
        public async Task<(bool exists, string binNum)> CheckBinExistsAsync(string warehouseCode, string binNum)
        {
            if (string.IsNullOrEmpty(binNum))
                return (false, binNum);

            // 注意：此处不吞异常，理由同 CheckWarehouseExistsAsync。
            string filter = string.IsNullOrEmpty(warehouseCode)
                ? ODataFilterBuilder.Eq("BinNum", binNum)
                : ODataFilterBuilder.And(ODataFilterBuilder.Eq("BinNum", binNum), ODataFilterBuilder.Eq("WarehouseCode", warehouseCode));
            string url = ODataFilterBuilder.BuildUrl("Erp.BO.WhseBinSvc/WhseBins", filter, top: 1);

            string responseJson = await _authService.GetODataJsonAsync(url);

            if (string.IsNullOrEmpty(responseJson))
                return (false, binNum);

            var jObj = JObject.Parse(responseJson);
            var values = jObj["value"] as JArray;
            bool exists = values != null && values.Any();

            return (exists, binNum);
        }

        #endregion

        #endregion
    }
}