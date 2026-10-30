using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WarehouseScannerCore.Configuration;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;
namespace WarehouseScanner.Services
{
    /// <summary>业务方法 - 库存</summary>
    public partial class EpicorAuthService
    {
        #region 业务方法 - 库存相关
        /// <summary>查询物料库存（单页）</summary>
        public async Task<List<PartBinSearchResult>> GetPartNumInventory(
            string partNum,
            string? whseCode = null,
            string? binNum = null,
            string? lotNumber = null,
            int pageSize = 50,
            int absolutePage = 1,
            CancellationToken cancellationToken = default)
            => (await GetPartNumInventoryPageAsync(
                partNum, whseCode, binNum, lotNumber, pageSize, absolutePage, cancellationToken)).Rows;

        /// <summary>
        /// 查询物料库存的单个分页，并尽量读取服务端"是否还有下一页"标记。
        /// <c>HasMore</c> 为 null 表示响应未提供可识别的 total/hasMore/nextPage 字段，
        /// 调用方应回退到"本页为空才结束"的判定。
        /// </summary>
        private async Task<(List<PartBinSearchResult> Rows, bool? HasMore)> GetPartNumInventoryPageAsync(
            string partNum,
            string? whseCode,
            string? binNum,
            string? lotNumber,
            int pageSize,
            int absolutePage,
            CancellationToken cancellationToken)
        {
            ClearApiError(); // 重置错误状态
            try
            {
                if (string.IsNullOrWhiteSpace(partNum)) return (new List<PartBinSearchResult>(), null);

                // 安全：所有字面量统一走 EscapeString 转义（OData 单引号 → 两个单引号），
                // 并对标识类字段做白名单校验，防止扫码枪 / 前端输入造成 OData 注入。
                if (!ODataFilterBuilder.IsValidPartNum(partNum))
                {
                    _logger.LogWarning("物料编码格式非法，跳过库存查询 PartNum:{PartNum}", partNum);
                    return (new List<PartBinSearchResult>(), null);
                }

                var conditions = new List<string>();
                conditions.Add($"PartNum = '{ODataFilterBuilder.EscapeString(partNum)}'");

                if (!string.IsNullOrEmpty(whseCode))
                {
                    if (!ODataFilterBuilder.IsValidCode(whseCode))
                    {
                        _logger.LogWarning("仓库编码格式非法，已忽略该过滤条件 WhseCode:{WhseCode}", whseCode);
                    }
                    else
                    {
                        conditions.Add($"WhseCode = '{ODataFilterBuilder.EscapeString(whseCode)}'");
                    }
                }
                if (!string.IsNullOrEmpty(binNum))
                {
                    if (!ODataFilterBuilder.IsValidCode(binNum))
                    {
                        _logger.LogWarning("库位编码格式非法，已忽略该过滤条件 BinNum:{BinNum}", binNum);
                    }
                    else
                    {
                        conditions.Add($"BinNum = '{ODataFilterBuilder.EscapeString(binNum)}'");
                    }
                }
                if (!string.IsNullOrEmpty(lotNumber))
                {
                    if (!ODataFilterBuilder.IsValidCode(lotNumber))
                    {
                        _logger.LogWarning("批次号格式非法，已忽略该过滤条件 LotNumber:{LotNumber}", lotNumber);
                    }
                    else
                    {
                        conditions.Add($"LotNumber = '{ODataFilterBuilder.EscapeString(lotNumber)}'");
                    }
                }
                string whereClause = string.Join(" AND ", conditions);
                
                var reqBody = new { pageSize, absolutePage, whereClause };
                string json = JsonConvert.SerializeObject(reqBody, _jsonSettings);

                string body = await PostODataJsonAsync("Erp.BO.PartBinSearchSvc/GetPartBinSearch", json, cancellationToken, requireLicense: false);

                // 【步骤1：检查 API 是否返回错误 JSON】
                if (CheckEpicorApiError(body, $"GetPartNumInventory(PartNum={partNum})", out _))
                    return (new List<PartBinSearchResult>(), null);

                // 【步骤2：无错误时才执行业务逻辑解析】
                var rows = ParsePartBinSearchResponse(body, out bool? hasMore);
                return (rows, hasMore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询物料库存异常 PartNum:{PartNum}", partNum);
                SetApiError("库存查询失败，请稍后重试");
                return (new List<PartBinSearchResult>(), null);
            }
        }

        public async Task<List<PartBinSearchResult>> GetPartBinInventory(string partNum, int pageSize = 50, int absolutePage = 1, CancellationToken cancellationToken = default)
            => await GetPartNumInventory(partNum, null, null, null, pageSize, absolutePage, cancellationToken);

        /// <summary>库存汇总翻页上限（防止异常数据导致死循环）</summary>
        private const int MaxInventoryPages = 20;

        /// <summary>
        /// 查询物料的全部 PartBin 行（自动翻页）。
        /// 结束条件优先采用服务端显式标记（total/hasMore/nextPage），
        /// 未提供标记时回退为"本页为空才结束"——不能用"本页行数少于请求 pageSize"判定，
        /// 否则服务端若按更小的页大小截断，会提前结束并静默少算库存。
        /// 所有分支都受 <see cref="MaxInventoryPages"/> 上限保护，避免死循环。
        /// </summary>
        public async Task<List<PartBinSearchResult>> GetPartNumInventoryAll(
            string partNum,
            string? whseCode = null,
            string? binNum = null,
            string? lotNumber = null,
            CancellationToken cancellationToken = default)
        {
            var all = new List<PartBinSearchResult>();
            for (int page = 1; page <= MaxInventoryPages; page++)
            {
                var (rows, hasMore) = await GetPartNumInventoryPageAsync(
                    partNum, whseCode, binNum, lotNumber, LargePageSize, page, cancellationToken);
                if (rows == null || rows.Count == 0) break;

                all.AddRange(rows);
                if (hasMore == false) break;

                if (page == MaxInventoryPages)
                    _logger.LogWarning("库存翻页达到上限 {MaxPages} 页，结果可能不完整: PartNum={PartNum}", MaxInventoryPages, partNum);
            }
            return all;
        }

        private List<PartBinSearchResult> ParsePartBinSearchResponse(string json, out bool? hasMore)
        {
            hasMore = null;
            var list = new List<PartBinSearchResult>();
            if (string.IsNullOrEmpty(json)) return list;

            try
            {
                var jObj = JObject.Parse(json);
                var arr = jObj["returnObj"]?["PartBinSearch"] as JArray;
                if (arr == null) return list;

                hasMore = ReadHasMoreFlag(jObj);

                list.AddRange(arr.Select(item => new PartBinSearchResult
                {
                    Company = item.Value<string>("Company") ?? string.Empty,
                    PartNum = item.Value<string>("PartNum") ?? string.Empty,
                    Plant = item.Value<string>("Plant") ?? string.Empty,
                    WhseCode = item.Value<string>("WhseCode") ?? string.Empty,
                    BinNum = item.Value<string>("BinNum") ?? string.Empty,
                    LotNumber = item.Value<string>("LotNumber") ?? string.Empty,
                    DimCode = item.Value<string>("DimCode") ?? string.Empty,
                    QtyOnHand = item.Value<decimal?>("QtyOnHand") ?? 0m,
                    PartDesc = item.Value<string>("PartDesc") ?? string.Empty,
                    PlantName = item.Value<string>("PlantName") ?? string.Empty,
                    WhseCodeDesc = item.Value<string>("WhseCodeDesc") ?? string.Empty,
                    BinDesc = item.Value<string>("BinDesc") ?? string.Empty,
                    LotNumberDesc = item.Value<string>("LotNumberDesc") ?? string.Empty,
                    DimCodeDesc = item.Value<string>("DimCodeDesc") ?? string.Empty,
                    NonNettable = item.Value<bool?>("NonNettable") ?? false,
                    IUM = item.Value<string>("IUM") ?? string.Empty,
                    BinType = item.Value<string>("BinType") ?? string.Empty,
                    ContractID = item.Value<string>("ContractID") ?? string.Empty,
                    PCID = item.Value<string>("PCID") ?? string.Empty,
                    SendToFSA = item.Value<bool?>("SendToFSA") ?? false,
                    QtyAllocated = item.Value<decimal?>("QtyAllocated") ?? 0m,
                    AttributeSetID = item.Value<int?>("AttributeSetID") ?? 0,
                    AttributeSetDescription = item.Value<string>("AttributeSetDescription") ?? string.Empty,
                    AttributeSetShortDescription = item.Value<string>("AttributeSetShortDescription") ?? string.Empty,
                    RevisionNum = item.Value<string>("RevisionNum") ?? string.Empty,
                    SysRowID = item.Value<string>("SysRowID") ?? string.Empty,
                    RowMod = item.Value<string>("RowMod") ?? string.Empty
                }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析库存数据失败");
                SetApiError("库存数据解析失败，请稍后重试");
            }
            return list;
        }

        /// <summary>
        /// 读取分页响应中的"是否还有下一页"标记。
        /// Epicor 分页 BO 常见 out 参数为 morePages；其余接口可能用 hasMore / hasNextPage，
        /// 或返回 nextPage 页码（&gt;0 表示还有下一页）。
        /// 找不到可识别字段时返回 null，由调用方回退到"空页才结束"。
        /// </summary>
        private static bool? ReadHasMoreFlag(JObject root)
        {
            var scopes = new[] { root["returnObj"] as JObject, root };
            var boolNames = new[] { "morePages", "MorePages", "hasMore", "HasMore", "hasNextPage", "HasNextPage" };
            var pageNames = new[] { "nextPage", "NextPage" };

            foreach (var scope in scopes)
            {
                if (scope == null) continue;

                foreach (var name in boolNames)
                {
                    var token = scope[name];
                    if (token != null && token.Type == JTokenType.Boolean)
                        return token.Value<bool>();
                }

                foreach (var name in pageNames)
                {
                    var token = scope[name];
                    if (token != null && token.Type == JTokenType.Integer)
                        return token.Value<int>() > 0;
                }
            }

            return null;
        }

        public async Task<List<SimpleBinInventory>> GetSimplePartBinInventory(string partNum, CancellationToken cancellationToken = default)
        {
            // 自动翻页取全部 PartBin 行：只取第 1 页会在库存行数超过一页时少算
            var data = await GetPartNumInventoryAll(partNum, cancellationToken: cancellationToken);
            return data.Select(x => new SimpleBinInventory
            {
                PartNum = x.PartNum,
                PartDesc = x.PartDesc,
                Plant = x.Plant,
                WhseCode = x.WhseCode,
                WhseCodeDesc = x.WhseCodeDesc,
                BinNum = x.BinNum,
                BinDesc = x.BinDesc,
                LotNumber = x.LotNumber,
                QtyOnHand = x.QtyOnHand,
                QtyAllocated = x.QtyAllocated,
                AvailableQty = x.QtyOnHand - x.QtyAllocated,
                IUM = x.IUM
            }).ToList();
        }

        /// <summary>
        /// 调用 Epicor BAQ Inve_view 查询 PartBin 库存位置信息
        /// </summary>
        /// <param name="partNum">物料代码（映射 BAQ 参数 PartBin.PartNum）</param>
        /// <param name="lotNum">批次号/工单号（映射 BAQ 参数 PartBin.LotNum）</param>
        /// <param name="warehouseCode">仓库代码（映射 BAQ 参数 PartBin.WarehouseCode，可选）</param>
        /// <param name="binNum">库位（映射 BAQ 参数 PartBin.BinNum，可选）</param>
        /// <returns>单位、仓库代码、库位、在库数量；查不到返回 null</returns>
        public async Task<InveViewLocationResult?> QueryInveViewByBAQAsync(
            string partNum, string lotNum, string? warehouseCode = null, string? binNum = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(partNum) || string.IsNullOrWhiteSpace(lotNum))
                return null;

            try
            {
                var baqId = "Inve_view";

                var parameters = new List<object>
                {
                    new { ParameterID = "PartNum", ParameterValue = partNum, ValueType = "string", IsEmpty = false },
                    new { ParameterID = "LotNum", ParameterValue = lotNum, ValueType = "string", IsEmpty = false }
                };
                if (!string.IsNullOrWhiteSpace(warehouseCode))
                {
                    parameters.Add(new { ParameterID = "WarehouseCode", ParameterValue = warehouseCode.Trim(), ValueType = "string", IsEmpty = false });
                }
                if (!string.IsNullOrWhiteSpace(binNum))
                {
                    parameters.Add(new { ParameterID = "BinNum", ParameterValue = binNum.Trim(), ValueType = "string", IsEmpty = false });
                }
                AddPlantParameter(parameters);

                string json = JsonConvert.SerializeObject(new
                {
                    queryID = baqId,
                    executionParams = new
                    {
                        ExecutionParameter = parameters.ToArray()
                    }
                }, _jsonSettings);

                string body = await PostBoJsonAsync(
                    "Ice.BO.DynamicQuerySvc/ExecuteByID", json, cancellationToken, requireLicense: false);

                if (string.IsNullOrEmpty(body))
                {
                    _logger.LogWarning("QueryInveViewByBAQ 返回空响应: PartNum={PartNum}, LotNum={LotNum}", partNum, lotNum);
                    return null;
                }

                // #region 业务 - BAQ 错误检测（统一使用 CheckEpicorApiError，兼容 RFC 9110 错误格式）
                if (CheckEpicorApiError(body, $"QueryInveViewByBAQ(PartNum={partNum}, LotNum={lotNum})", out _))
                    return null;

                var jObj = JObject.Parse(body);
                var arr = jObj["returnObj"]?["Results"] as JArray;
                if (arr == null || !arr.Any())
                {
                    _logger.LogWarning("BAQ Inve_view 查询无结果: PartNum={PartNum}, LotNum={LotNum}", partNum, lotNum);
                    return null;
                }

                // 同一 物料+批次 可能存在于多个库位：此处按首行回填仓库/库位（定位场景的既有约定），
                // 但多行时必须留痕，便于排查"回填到非预期库位"的问题。
                if (arr.Count > 1)
                {
                    _logger.LogWarning("BAQ Inve_view 匹配到 {Count} 行，按首行回填仓库/库位（多库位时请人工确认）: PartNum={PartNum}, LotNum={LotNum}",
                        arr.Count, partNum, lotNum);
                }

                var first = arr.First() as JObject;
                if (first == null) return null;

                var result = new InveViewLocationResult
                {
                    Unit = first["Part_IUM"]?.Value<string>() ?? first["IUM"]?.Value<string>() ?? string.Empty,
                    WarehouseCode = first["PartBin_WarehouseCode"]?.Value<string>() ?? first["WarehouseCode"]?.Value<string>() ?? string.Empty,
                    BinNum = first["PartBin_BinNum"]?.Value<string>() ?? first["BinNum"]?.Value<string>() ?? string.Empty,
                    OnhandQty = first["PartBin_OnhandQty"]?.Value<decimal?>() ?? first["OnhandQty"]?.Value<decimal?>() ?? 0m
                };

                _logger.LogInformation("BAQ Inve_view 查询成功: PartNum={PartNum}, LotNum={LotNum} → Unit={Unit}, WarehouseCode={WarehouseCode}, BinNum={BinNum}",
                    partNum, lotNum, result.Unit, result.WarehouseCode, result.BinNum);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueryInveViewByBAQ 异常: PartNum={PartNum}, LotNum={LotNum}", partNum, lotNum);
                SetApiError("库存位置查询失败，请稍后重试");
                return null;
            }
        }

        /// <summary>
        /// 调用 Epicor BAQ SO_SchdUpdate 查询销售订单发货行明细（PONum + OrderLine + OrderRelNum）。
        /// 返回订单号、订单行、发货行、工单号(jobnum_c)、
        /// 计划出货数量（OrderRel_SellingReqQty）、已发货数量（Calculated_reltotalqty）。
        /// BAQ 其他参数（OrderNum/PartNum/Customer/日期范围）不传，由 Epicor 按 IsEmpty 跳过过滤。
        /// </summary>
        /// <param name="poNum">客户 PO 号（映射 BAQ 参数 PONum）</param>
        /// <param name="orderLine">订单行号</param>
        /// <param name="orderRelNum">发货行号</param>
        /// <returns>发货行明细；查不到返回 null</returns>
        public async Task<ShippingScheduleResult?> QuerySOSchdUpdateByBAQAsync(
            string poNum, int orderLine, int orderRelNum,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(poNum) || orderLine < 0 || orderRelNum < 0)
                return null;

            // 与同文件其他 BAQ 方法（如 ListInveViewInventoryByBAQAsync）保持一致：
            // 入口清空错误状态，使 HasApiError 只表达"本次调用"的结果，
            // 否则上一次调用的失败状态会粘住本次调用，让调用方误判为接口故障
            ClearApiError();
            try
            {
                const string baqId = "SO_SchdUpdate";

                var parameters = new List<object>
                {
                    new { ParameterID = "PONum", ParameterValue = poNum, ValueType = "string", IsEmpty = false },
                    new { ParameterID = "OrderLine", ParameterValue = orderLine.ToString(CultureInfo.InvariantCulture), ValueType = "string", IsEmpty = false },
                    new { ParameterID = "OrderRelNum", ParameterValue = orderRelNum.ToString(CultureInfo.InvariantCulture), ValueType = "string", IsEmpty = false }
                };
                AddPlantParameter(parameters);

                string json = JsonConvert.SerializeObject(new
                {
                    queryID = baqId,
                    executionParams = new
                    {
                        ExecutionParameter = parameters.ToArray()
                    }
                }, _jsonSettings);

                string body = await PostBoJsonAsync(
                    "Ice.BO.DynamicQuerySvc/ExecuteByID", json, cancellationToken, requireLicense: false);

                if (string.IsNullOrEmpty(body))
                {
                    _logger.LogWarning("QuerySOSchdUpdateByBAQ 返回空响应: PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum}",
                        poNum, orderLine, orderRelNum);
                    return null;
                }

                if (CheckEpicorApiError(body, $"QuerySOSchdUpdateByBAQ(PONum={poNum}, OrderLine={orderLine}, OrderRelNum={orderRelNum})", out _))
                    return null;

                var jObj = JObject.Parse(body);
                var arr = jObj["returnObj"]?["Results"] as JArray;
                if (arr == null || !arr.Any())
                {
                    _logger.LogWarning("BAQ SO_SchdUpdate 查询无结果: PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum}",
                        poNum, orderLine, orderRelNum);
                    return null;
                }

                var first = arr.First() as JObject;
                if (first == null) return null;

                // 计划出货数量取 OrderRel_SellingReqQty（该发货行的订单需求总量，不随发货变化）。
                // 不能用 Calculated_QtyReq + Calculated_reltotalqty：Calculated_QtyReq 是不随发货减少的常量，
                // reltotalqty 随每次发货累加，两者相加会把已发货数量重复计入
                // （实机日志：同一发货行 ShippedQty 0→4 时 PlanShipQty 460→464，实际应为 460）。
                // 注意：Value<T> 对 null token 会抛 InvalidCastException，必须用 Value<T?>
                decimal sellingReqQty = first["OrderRel_SellingReqQty"]?.Value<decimal?>() ?? 0;
                decimal relTotalQty = first["Calculated_reltotalqty"]?.Value<decimal?>() ?? 0;

                var result = new ShippingScheduleResult
                {
                    OrderNum = first["OrderRel_OrderNum"]?.Value<string>() ?? first["OrderHed_OrderNum"]?.Value<string>() ?? string.Empty,
                    OrderLine = first["OrderRel_OrderLine"]?.Value<int?>() ?? 0,
                    OrderRelNum = first["OrderRel_OrderRelNum"]?.Value<int?>() ?? 0,
                    JobNum = first["OrderRel_jobnum_c"]?.Value<string>() ?? string.Empty,
                    PlanShipQty = sellingReqQty,
                    ShippedQty = relTotalQty
                };

                _logger.LogInformation("BAQ SO_SchdUpdate 查询成功: PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum} → OrderNum={OrderNum}, JobNum={JobNum}, PlanShipQty={PlanShipQty}, ShippedQty={ShippedQty}",
                    poNum, orderLine, orderRelNum, result.OrderNum, result.JobNum, result.PlanShipQty, result.ShippedQty);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QuerySOSchdUpdateByBAQ 异常: PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum}",
                    poNum, orderLine, orderRelNum);
                SetApiError("订单发货信息查询失败，请稍后重试");
                return null;
            }
        }

        /// <summary>
        /// 【列表版，用于周期盘点维护查询】调用 Epicor BAQ Inve_view 查询多条库存明细（按 物料+仓库+库位）
        /// 核心逻辑直接复用已有的 QueryInveViewByBAQAsync：BAQ 名称、ParameterID/ParameterValue/ValueType/IsEmpty 参数格式完全一致。
        /// 解析字段使用 Epicor BAQ 返回的 PartBin_* 前缀：尤其 PartBin_LotNum = [PartBin].[LotNum]。
        /// </summary>
        /// <param name="partNum">物料代码（映射 BAQ 参数 PartNum）</param>
        /// <param name="warehouseCode">仓库代码（映射 BAQ 参数 WarehouseCode，可选）</param>
        /// <param name="binNum">库位（映射 BAQ 参数 BinNum，可选）</param>
        /// <param name="lotNum">批次号（映射 BAQ 参数 LotNum，可选）</param>
        public async Task<List<InventoryModel>> ListInveViewInventoryByBAQAsync(
            string partNum,
            string warehouseCode,
            string? binNum = null,
            string? lotNum = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(partNum)
                && string.IsNullOrWhiteSpace(warehouseCode)
                && string.IsNullOrWhiteSpace(binNum)
                && string.IsNullOrWhiteSpace(lotNum))
                return new List<InventoryModel>();

            ClearApiError();
            try
            {
                const string baqId = "Inve_view";

                // 复用已有 QueryInveViewByBAQAsync 的 ParameterID / ParameterValue / ValueType / IsEmpty 参数格式
                var parameters = new List<object>();
                if (!string.IsNullOrWhiteSpace(partNum))
                {
                    parameters.Add(new { ParameterID = "PartNum", ParameterValue = partNum.Trim(), ValueType = "string", IsEmpty = false });
                }
                if (!string.IsNullOrWhiteSpace(warehouseCode))
                {
                    parameters.Add(new { ParameterID = "WarehouseCode", ParameterValue = warehouseCode.Trim(), ValueType = "string", IsEmpty = false });
                }
                if (!string.IsNullOrWhiteSpace(binNum))
                {
                    parameters.Add(new { ParameterID = "BinNum", ParameterValue = binNum.Trim(), ValueType = "string", IsEmpty = false });
                }
                if (!string.IsNullOrWhiteSpace(lotNum))
                {
                    // 批次参数名与 QueryInveViewByBAQAsync 保持一致（BAQ Inve_view 的 LotNum 参数）
                    parameters.Add(new { ParameterID = "LotNum", ParameterValue = lotNum.Trim(), ValueType = "string", IsEmpty = false });
                }
                AddPlantParameter(parameters);

                string json = JsonConvert.SerializeObject(new
                {
                    queryID = baqId,
                    executionParams = new { ExecutionParameter = parameters.ToArray() }
                }, _jsonSettings);

                _logger.LogInformation("Inve_view 明细库存查询(列表版 ListInveViewInventoryByBAQAsync): PartNum={PartNum}, Whse={Whse}, Bin={Bin}, Lot={Lot}", partNum, warehouseCode, binNum, lotNum);

                string body = await PostBoJsonAsync(
                    "Ice.BO.DynamicQuerySvc/ExecuteByID", json, cancellationToken, requireLicense: false);

                if (CheckEpicorApiError(body, $"ListInveViewInventoryByBAQAsync(PartNum={partNum}, Whse={warehouseCode})", out _))
                    return new List<InventoryModel>();

                var jObj = JObject.Parse(body);
                var arr = jObj["returnObj"]?["Results"] as JArray;
                if (arr == null || !arr.Any())
                {
                    _logger.LogWarning("Inve_view 明细库存无结果(列表版): PartNum={PartNum}, Whse={Whse}", partNum, warehouseCode);
                    return new List<InventoryModel>();
                }

                // 解析字段统一使用 BAQ 返回的 PartBin_* / Part_* 前缀，尤其 [PartBin].[LotNum]
                return arr.Select(item => new InventoryModel
                {
                    PartNum = item["PartBin_PartNum"]?.Value<string>() ?? item["PartNum"]?.Value<string>() ?? string.Empty,
                    PartDescription = item["Part_PartDescription"]?.Value<string>() ?? item["PartDescription"]?.Value<string>() ?? string.Empty,
                    WarehouseCode = item["PartBin_WarehouseCode"]?.Value<string>() ?? item["WarehouseCode"]?.Value<string>() ?? string.Empty,
                    BinNum = item["PartBin_BinNum"]?.Value<string>() ?? item["BinNum"]?.Value<string>() ?? string.Empty,
                    LotNum = item["PartBin_LotNum"]?.Value<string>() ?? item["LotNum"]?.Value<string>() ?? string.Empty,
                    IUM = item["Part_IUM"]?.Value<string>() ?? item["IUM"]?.Value<string>() ?? string.Empty,
                    OnhandQty = item["PartBin_OnhandQty"]?.Value<decimal?>() ?? item["OnhandQty"]?.Value<decimal?>() ?? 0m,
                    FirstRefDate = item["PartLot_FirstRefDate"]?.Value<string>() ?? string.Empty
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inve_view 明细库存查询异常(列表版) PartNum={PartNum}, Whse={Whse}", partNum, warehouseCode);
                SetApiError("库存明细查询失败，请稍后重试");
                return new List<InventoryModel>();
            }
        }

        #endregion
    }
}