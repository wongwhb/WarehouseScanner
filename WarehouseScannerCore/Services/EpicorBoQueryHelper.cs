using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WarehouseScanner.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// Epicor BO 查询辅助类。
    ///
    /// 目的：把「拼接 Erp.BO.* GetRows 的 OData URL 并调用 Epicor」这一职责从 Controller
    ///       移出，Controller 不直接构造 / 调用 Epicor API。
    ///
    /// 设计取舍（行为等价优先）：
    ///   1. 方法为无状态静态方法，且<b>接收调用方传入的 EpicorAuthService 实例</b>，
    ///      不自行创建 auth。原因是这些调用点之后会用同一个实例的
    ///      HasApiError / LastApiError 判断 Epicor 业务错误；
    ///      若改为内部新建实例（如 EpicorQueryGateway 的做法），错误状态将落在另一个实例上，
    ///      会静默破坏错误传递 —— 那属于改变业务逻辑。
    ///   2. 参数校验（仓库代码白名单、物料编码白名单、PONUM 数字校验）仍保留在调用方，
    ///      以保持其原有的拒绝分支与本地化提示完全不变；本类只负责 OData 转义、
    ///      URL 组装与调用，输入必须是调用方校验过的合法值。
    ///   3. 返回值为 Epicor 原始响应字符串，解析与日志仍由调用方处理，逻辑不变。
    /// </summary>
    public static class EpicorBoQueryHelper
    {
        // GetRows 翻页参数：每页 50 行，最多 20 页（防止异常条件导致无限拉取）
        private const int RowsPageSize = 50;
        private const int RowsMaxPages = 20;

        /// <summary>
        /// 翻页拉取 GetRows 全部数据并聚合 returnObj 下的数组。
        /// 以 primaryEntityName 对应数组的行数判断是否还有下一页（不足一页即停止）。
        /// 首页失败原样返回响应（保持调用方判错链路不变）；第 2 页起失败返回空串，
        /// 由调用方走接口故障分支——上游只在响应为空时查 HasApiError，若返回部分数据会被静默使用。
        /// </summary>
        private static async Task<string> GetAllRowsAsync(
            EpicorAuthService authService, string urlWithoutPaging, string primaryEntityName)
        {
            JObject? mergedReturnObj = null;
            string firstPageResponse = string.Empty;

            for (int page = 1; page <= RowsMaxPages; page++)
            {
                var response = await authService.GetBoJsonAsync(
                    $"{urlWithoutPaging}&pageSize={RowsPageSize}&absolutePage={page}");

                if (page == 1) firstPageResponse = response;
                if (string.IsNullOrWhiteSpace(response))
                    return mergedReturnObj == null ? response : string.Empty;

                JObject parsed;
                try
                {
                    parsed = JObject.Parse(response);
                }
                catch (Exception)
                {
                    return mergedReturnObj == null ? response : string.Empty;
                }

                if (parsed["returnObj"] is not JObject returnObj)
                {
                    return mergedReturnObj == null ? response : string.Empty;
                }

                mergedReturnObj ??= new JObject();
                int primaryCount = 0;

                foreach (var property in returnObj.Properties())
                {
                    if (property.Value is JArray array)
                    {
                        var target = mergedReturnObj[property.Name] as JArray;
                        if (target == null)
                        {
                            target = new JArray();
                            mergedReturnObj[property.Name] = target;
                        }

                        foreach (var item in array)
                            target.Add(item.DeepClone());

                        if (string.Equals(property.Name, primaryEntityName, StringComparison.OrdinalIgnoreCase))
                            primaryCount = array.Count;
                    }
                    else if (!mergedReturnObj.ContainsKey(property.Name))
                    {
                        mergedReturnObj[property.Name] = property.Value.DeepClone();
                    }
                }

                if (primaryCount < RowsPageSize) break;
            }

            if (mergedReturnObj == null) return firstPageResponse;

            return new JObject { ["returnObj"] = mergedReturnObj }.ToString(Formatting.None);
        }

        /// <summary>
        /// 周期盘点（CCCountCycleSvc）表头查询。
        /// 等价于原 CountViewController 内联实现：
        /// whereClauseCCHdr = CCYear={年} and WarehouseCode='{仓库}' and CycleDate='{yyyy-MM-dd}'
        /// </summary>
        public static Task<string> GetCountCycleRowsAsync(
            EpicorAuthService authService, string warehouseCode, DateTime cycleDate)
        {
            if (authService == null) throw new ArgumentNullException(nameof(authService));

            // 日期必须用 InvariantCulture 格式化，避免非中文/欧洲区域下被格式化成带本地分隔符的形式
            var cycleDateStr = cycleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var whereClause =
                $"CCYear={cycleDate.Year} and WarehouseCode='{ODataFilterBuilder.EscapeString(warehouseCode)}' " +
                $"and CycleDate='{ODataFilterBuilder.EscapeString(cycleDateStr)}'";
            var encodedWhereClause = Uri.EscapeDataString(whereClause);

            var apiUrl = $"Erp.BO.CCCountCycleSvc/GetRows?whereClauseCCHdr={encodedWhereClause}" +
                         "&whereClauseCCDtl=&whereClauseCCPCID=&whereClauseLegalNumGenOpts=";

            return GetAllRowsAsync(authService, apiUrl, "CCHdr");
        }

        /// <summary>
        /// 盘点标签（CountTagSvc）查询。
        /// 等价于原 CountTagController 内联实现：whereClauseCCTag = PartNum='{物料}'
        /// </summary>
        public static Task<string> GetCountTagRowsAsync(EpicorAuthService authService, string materialCode)
        {
            if (authService == null) throw new ArgumentNullException(nameof(authService));

            var whereClause = $"PartNum='{ODataFilterBuilder.EscapeString(materialCode)}'";
            var encodedWhereClause = Uri.EscapeDataString(whereClause);

            var apiUrl = $"Erp.BO.CountTagSvc/GetRows?whereClauseCCTag={encodedWhereClause}" +
                         "&whereClauseCCTagWrkSht=";

            return GetAllRowsAsync(authService, apiUrl, "CCTag");
        }

        /// <summary>
        /// PO 明细行搜索（PODetailLineSearchSvc）查询。
        /// 等价于原 ReceivinController.SearchPOLineAsync 中的 URL 构造部分：
        /// 以 PONUM 为基础，按需追加 POLine / OpenLine 条件，用 " and " 连接。
        /// 注意：PONUM 为无引号数字字面量，调用方必须先做数字白名单校验（本方法不做引号转义）。
        /// </summary>
        public static Task<string> GetPoDetailLineRowsAsync(
            EpicorAuthService authService, string poNum, int? poLine = null, bool? openLine = null)
        {
            if (authService == null) throw new ArgumentNullException(nameof(authService));

            var whereParts = new List<string> { $"PONUM={poNum}" };
            if (poLine.HasValue)
                whereParts.Add($"POLine={poLine.Value}");
            if (openLine.HasValue)
                whereParts.Add($"OpenLine={(openLine.Value ? "true" : "false")}");

            var whereClause = string.Join(" and ", whereParts);
            var encodedWhere = Uri.EscapeDataString(whereClause);

            var url = $"Erp.BO.PODetailLineSearchSvc/GetRows?whereClausePODetail={encodedWhere}" +
                      $"&pageSize=50&absolutePage=1";

            return authService.GetBoJsonAsync(url);
        }
    }
}
