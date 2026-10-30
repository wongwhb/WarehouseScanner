using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;
namespace WarehouseScannerCore.Controllers
{
    /// <summary>OpenPODetail BAQ 查询</summary>
    public partial class ReceivinController
    {
        #region OpenPODetail BAQ - PO明细查询

        /// <summary>
        /// 通过 OpenPODetail BAQ 查询PO明细信息
        /// </summary>
        /// <param name="authService">Epicor认证服务</param>
        /// <param name="poNo">采购订单号</param>
        /// <param name="poLine">PO行号</param>
        /// <param name="partNum">物料代码</param>
        /// <returns>BAQ查询结果，包含订单数量和已收数量</returns>
        private async Task<POBAQResult?> QueryPOInfoByBAQAsync(
            EpicorAuthService authService, string poNo, int? poLine = null, string? partNum = null)
        {
            try
            {
                var baqId = "OpenPODetail";

                var parameters = new List<object>
                {
                    new { ParameterID = "PoNO", ParameterValue = poNo, ValueType = "string", IsEmpty = false }
                };

                if (poLine.HasValue)
                    parameters.Add(new { ParameterID = "PoLine", ParameterValue = poLine.Value.ToString(), ValueType = "int", IsEmpty = false });

                if (!string.IsNullOrEmpty(partNum))
                    parameters.Add(new { ParameterID = "PartNum", ParameterValue = partNum, ValueType = "string", IsEmpty = false });

                // 多站点隔离：配置了 Plant 时收敛 BAQ 数据可见性到当前工厂
                if (!string.IsNullOrEmpty(authService.Plant))
                    parameters.Add(new { ParameterID = "Plant", ParameterValue = authService.Plant, ValueType = "string", IsEmpty = false });

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    queryID = baqId,
                    executionParams = new
                    {
                        ExecutionParameter = parameters.ToArray()
                    }
                });


                string body = await authService.PostBoJsonAsync(
                    "Ice.BO.DynamicQuerySvc/ExecuteByID", json);

                if (string.IsNullOrEmpty(body))
                {
                    _logger.LogWarning("QueryPOInfoByBAQ 返回空响应: PoNo={PoNo}", poNo);
                    return null;
                }

                // 统一走 EpicorJsonHelper 的错误识别：原先用字符串 contains 判断，
                // 响应体中出现该字样的正常数据（如备注字段）会被误判为接口错误。
                var (jObj, hasError, errorMessage) = EpicorJsonHelper.TryParseResponse(body);
                if (hasError)
                {
                    _logger.LogError("BAQ查询返回错误: {Error}", errorMessage);
                    return null;
                }
                var arr = jObj["returnObj"]?["Results"] as JArray;
                if (arr == null || !arr.Any())
                {
                    _logger.LogWarning("BAQ查询无结果: PoNo={PoNo}, PoLine={PoLine}, PartNum={PartNum}", poNo, poLine, partNum);
                    return null;
                }

                var first = arr.First() as JObject;
                if (first == null)
                {
                    return null;
                }

                // 解析 BAQ 返回字段 (使用带 PORel_ 前缀的字段名)
                string poNoResult = first["PORel_PONum"]?.Value<string>() ?? first["PoNo"]?.Value<string>() ?? poNo;
                int poLineResult = 0;
                var poLineToken = first["PORel_POLine"] ?? first["PoLine"];
                if (poLineToken != null)
                {
                    if (poLineToken.Type == JTokenType.Integer)
                        poLineResult = poLineToken.Value<int>();
                    else
                        int.TryParse(poLineToken.Value<string>(), out poLineResult);
                }

                string partNumResult = first["PODetail_PartNum"]?.Value<string>() ?? first["PartNum"]?.Value<string>() ?? string.Empty;

                // 采购订单单位：BAQ 列名随配置不同会有差异，按优先级多字段名兜底解析
                string iumResult = first["PODetail_IUM"]?.Value<string>()
                    ?? first["PORel_IUM"]?.Value<string>()
                    ?? first["Part_IUM"]?.Value<string>()
                    ?? first["IUM"]?.Value<string>()
                    ?? string.Empty;

                decimal relQty = 0;
                var relQtyToken = first["PORel_RelQty"] ?? first["RelQty"];
                if (relQtyToken != null)
                {
                    if (relQtyToken.Type == JTokenType.Float || relQtyToken.Type == JTokenType.Integer)
                        relQty = relQtyToken.Value<decimal>();
                    else
                        // Epicor 数量字符串固定 "." 小数点，必须 InvariantCulture 解析，
                        //     防止逗号小数文化（de-DE 等）下静默解析为 0
                        decimal.TryParse(relQtyToken.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out relQty);
                }

                decimal receivedQty = 0;
                var recvQtyToken = first["PORel_ReceivedQty"] ?? first["ReceivedQty"];
                if (recvQtyToken != null)
                {
                    if (recvQtyToken.Type == JTokenType.Float || recvQtyToken.Type == JTokenType.Integer)
                        receivedQty = recvQtyToken.Value<decimal>();
                    else
                        // 同上，InvariantCulture 解析
                        decimal.TryParse(recvQtyToken.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out receivedQty);
                }

                // 解析 VendorNum (BAQ字段: [Vendor].[VendorNum])
                int vendorNum = 0;
                var vendorNumToken = first["Vendor_VendorNum"] ?? first["VendorNum"];
                if (vendorNumToken != null)
                {
                    if (vendorNumToken.Type == JTokenType.Integer)
                        vendorNum = vendorNumToken.Value<int>();
                    else
                        int.TryParse(vendorNumToken.Value<string>(), out vendorNum);
                }

                // 解析 VendorID (BAQ字段: [Vendor].[VendorID])
                string vendorID = first["Vendor_VendorID"]?.Value<string>() ?? first["VendorID"]?.Value<string>() ?? string.Empty;

                // 解析 PORelNum (BAQ字段: [PORel].[PORelNum])
                int poRelNum = 0;
                var porelNumToken = first["PORel_PORelNum"] ?? first["PORelNum"];
                if (porelNumToken != null)
                {
                    if (porelNumToken.Type == JTokenType.Integer)
                        poRelNum = porelNumToken.Value<int>();
                    else
                        int.TryParse(porelNumToken.Value<string>(), out poRelNum);
                }

                #region PO / 行状态字段解析（仓库侧显式校验）

                bool? ParseNullableBool(JToken? token)
                {
                    if (token == null) return null;
                    if (token.Type == JTokenType.Boolean) return token.ToObject<bool>();
                    if (token.Type == JTokenType.Integer) return token.ToObject<int>() != 0;
                    var s = token.ToString();
                    if (bool.TryParse(s, out var b)) return b;
                    return null;
                }

                // PODetail.OpenLine：行级是否打开（OpenPODetail BAQ 标准列，缺列时 null 跳过该行校验）
                bool? openLine =
                    ParseNullableBool(first["PODetail_OpenLine"]) ??
                    ParseNullableBool(first["OpenLine"]);

                // POHeader.ApprovalStatus：PO 头审核状态（A=已审核，U=未审核）
                string approvalStatus =
                    first["POHeader_ApprovalStatus"]?.ToString() ??
                    first["ApprovalStatus"]?.ToString() ??
                    first["POHeader_PONumApprovalStatus"]?.ToString() ??
                    first["PONumApprovalStatus"]?.ToString() ??
                    string.Empty;

                #endregion

                var result = new POBAQResult
                {
                    PoNo = poNoResult,
                    PoLine = poLineResult,
                    PORelNum = poRelNum,
                    PartNum = partNumResult,
                    RelQty = relQty,
                    ReceivedQty = receivedQty,
                    VendorNum = vendorNum,
                    VendorID = vendorID,
                    IUM = iumResult,
                    OpenLine = openLine,
                    ApprovalStatus = approvalStatus
                };

                _logger.LogInformation("BAQ查询成功: PoNo={PoNo}, PoLine={PoLine}, PORelNum={PORelNum}, PartNum={PartNum}, IUM={IUM}, RelQty={RelQty}, ReceivedQty={ReceivedQty}, AvailableQty={AvailableQty}, VendorNum={VendorNum}, VendorID={VendorID}, OpenLine={OpenLine}, ApprovalStatus={ApprovalStatus}",
                    result.PoNo, result.PoLine, result.PORelNum, result.PartNum, result.IUM, result.RelQty, result.ReceivedQty, result.AvailableQty, result.VendorNum, result.VendorID,
                    result.OpenLine, result.ApprovalStatus);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueryPOInfoByBAQ 查询异常: PoNo={PoNo}, PoLine={PoLine}, PartNum={PartNum}", poNo, poLine, partNum);
                return null;
            }
        }

        #endregion
    }
}