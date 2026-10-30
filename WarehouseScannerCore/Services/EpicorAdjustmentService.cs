using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>
    /// Epicor 库存调整服务类
    /// 通过 InventoryQtyAdjSvc 实现单侧库存调整（仅对指定位置做一次数量调整，不涉及源/目标双侧对调）
    /// </summary>
    public class EpicorAdjustmentService
    {
        // Epicor 库存调整目标记录（第二条/增加方向）使用的 ReasonCode 常量——
        // 固定为 RC42，集中定义并注释，避免魔法字符串散落。
        private const string TargetAdjustmentReasonCode = "RC42";

        private readonly EpicorAuthService _authService;
        private readonly ILogger<EpicorAdjustmentService>? _logger;

        public EpicorAdjustmentService(EpicorAuthService authService, string company, ILogger<EpicorAdjustmentService>? logger = null)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _logger = logger;
        }

        #region 公共方法

        /// <summary>
        /// 执行单侧库存调整（仅对指定位置做一次数量调整）
        /// </summary>
        /// <param name="partNum">物料代码</param>
        /// <param name="quantity">调整数量（必须大于 0）</param>
        /// <param name="fromWarehouse">仓库</param>
        /// <param name="fromBinNum">库位</param>
        /// <param name="fromLotNum">批次</param>
        /// <param name="remark">备注（可传入调整原因）</param>
        /// <returns>最终保存后的响应 JObject</returns>
        public async Task<JObject> AdjustmentInventoryAsync(
            string partNum,
            decimal quantity,
            string fromWarehouse,
            string fromBinNum,
            string fromLotNum,
            string? remark = null)
        {
            if (quantity <= 0)
                throw new ArgumentException("调整数量必须大于0", nameof(quantity));

            try
            {
                // 调整数量
                // 原因写入服务端日志：ERP 侧只有固定 ReasonCode（RC42），不体现操作员填写的原因；
                // 换行会破坏按行日志，先清洗成单行。
                _logger?.LogInformation("物料 {PartNum}, 数量 {Quantity}, 仓库 {Whse}, 库位 {Bin}, 批次 {Lot}, 原因 {Remark}",
                    partNum, quantity, fromWarehouse, fromBinNum, fromLotNum,
                    string.IsNullOrWhiteSpace(remark) ? string.Empty : remark.Replace('\r', ' ').Replace('\n', ' ').Trim());
                return await AdjustInventoryAsync(partNum, quantity, fromWarehouse, fromBinNum, fromLotNum, remark);
            }
            catch (Exception ex)
            {
                // 如实描述：本方法不执行回滚（Epicor 调整可能已部分生效），需人工核对
                _logger?.LogError(ex, "库存调整失败，未执行回滚，请人工核对源库位库存。物料 {PartNum}, 数量 {Quantity}", partNum, quantity);

                throw;
            }
        }

        #endregion

        #region 私有调整方法（完整流程）

        /// <summary>
        /// 对单个位置执行库存调整（增加或减少）
        /// </summary>
        private async Task<JObject> AdjustInventoryAsync(
            string partNum,
            decimal adjustQty,
            string warehouseCode,
            string binNum,
            string lotNum,
            string? remark)
        {
            // 1. 获取调整数据集
            var ds = await GetInventoryQtyAdjForPartAsync(partNum, warehouseCode, binNum, lotNum);

            // 2. 设置调整数量并调用实时校验
            ds = await OnChangingAdjustQuantityAsync(ds, adjustQty);

            // 3. 负库存校验
           await NegativeInventoryTestAsync(ds);

            // 4. 最终校验准备
            ds = await PreSetInventoryQtyAdjAsync(ds);

            // 5. 执行调整
            var result = await SetInventoryQtyAdjAsync(ds);

            return result;
        }

        #endregion

        #region 私有API调用方法
        private async Task<JObject> GetInventoryQtyAdjForPartAsync(string partNum, string warehouseCode, string binNum, string lotNum)
        {
            var requestBody = new
            {
                partnumber = partNum,
                uomCode = "",
                wareHseCode = warehouseCode,
                binNum = binNum ?? "",
                lotNum = lotNum ?? "",
                ds = new
                {
                    InventoryQtyAdj = new object[] { }
                }
            };
            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InventoryQtyAdjSvc/GetInventoryQtyAdjForPart", jsonPayload);
            if (_authService.HasApiError)
                throw new InvalidOperationException($"Epicor API 调用失败：{_authService.LastApiError}");
            var response = ParseResponse(responseJson);
            var transformedDs = TransformInventoryQtyAdjResponse(response);
            return transformedDs;
        }
        private async Task<JObject> OnChangingAdjustQuantityAsync(JObject ds, decimal adjustQty)
        {
            var requestBody = new
            {
               adjustQuantity = adjustQty,
                ds = ds
            };
            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InventoryQtyAdjSvc/OnChangingAdjustQuantity", jsonPayload);
            if (_authService.HasApiError)
                throw new InvalidOperationException($"Epicor API 调用失败：{_authService.LastApiError}");
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        private async Task<JObject> NegativeInventoryTestAsync(JObject ds)
        {
            var inventoryQtyAdjArray = ds["InventoryQtyAdj"] as JArray;
            if (inventoryQtyAdjArray == null || inventoryQtyAdjArray.Count < 2)
                throw new InvalidOperationException("InventoryQtyAdj 表不存在或行数不足");

            var row = (JObject)inventoryQtyAdjArray[1];

            string partNum = row["PartNum"]?.ToString() ?? "";
            string whseCode = row["WareHseCode"]?.ToString() ?? "";
            string binNum = row["BinNum"]?.ToString() ?? "";
            string lotNum = row["LotNum"]?.ToString() ?? "";
            decimal adjustQuantity = row["AdjustQuantity"]?.Value<decimal>() ?? 0;
            int attributeSetID = row["AttributeSetID"]?.Value<int>() ?? 0;
            string pcPCID = row["PCID"]?.ToString() ?? "";

            string dimCode = row["UnitOfMeasure"]?.ToString() ?? "";
            // 单位换算系数从行数据读取：缺失/非法时回退 1（硬编码 1 会丢失非基本单位的换算）
            double dimConvFactor = row["DimConvFactor"]?.ToObject<double?>() ?? 1;
       

            var requestBody = new
            {
                pcPartNum = partNum,
                pcWhseCode = whseCode,
                pcBinNum = binNum,
                pcLotNum = lotNum,
                pcAttributeSetID = attributeSetID,
                pcPCID = pcPCID,
                pcDimCode = dimCode,
                pdDimConvFactor = dimConvFactor,
                ipSellingQuantity = adjustQuantity
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InventoryQtyAdjSvc/NegativeInventoryTest", jsonPayload);
            if (_authService.HasApiError)
                throw new InvalidOperationException($"Epicor API 调用失败：{_authService.LastApiError}");
            var response = ParseResponse(responseJson);

            if (response["parameters"]?["errorMessage"] != null)
            {
                string error = response["parameters"]!["errorMessage"]!.ToString();
                if (!string.IsNullOrEmpty(error))
                    throw new InvalidOperationException($"负库存校验失败: {error}");
            }

            return EpicorJsonHelper.ExtractDs(response);
        }

        private async Task<JObject> PreSetInventoryQtyAdjAsync(JObject ds)
        {
            var requestBody = new { ds = ds };
            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InventoryQtyAdjSvc/PreSetInventoryQtyAdj", jsonPayload);
            if (_authService.HasApiError)
                throw new InvalidOperationException($"Epicor API 调用失败：{_authService.LastApiError}");
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        private async Task<JObject> SetInventoryQtyAdjAsync(JObject ds)
        {
            if (ds["InventoryQtyAdj"] is JArray invAdjArray && invAdjArray.Count >= 2)
            {
                // 数组索引 1 = 第二条记录（目标位置增加方向）
                invAdjArray[1]["ReasonCode"] = TargetAdjustmentReasonCode;
            }

            var requestBody = new { ds = ds };
            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InventoryQtyAdjSvc/SetInventoryQtyAdj", jsonPayload);
            if (_authService.HasApiError)
                throw new InvalidOperationException($"Epicor API 调用失败：{_authService.LastApiError}");
            return ParseResponse(responseJson);
        }

        #endregion

        #region 辅助方法

        private JObject ParseResponse(string responseJson)
        {
            if (string.IsNullOrEmpty(responseJson))
                return null!;
            return JObject.Parse(responseJson);
        }

        private string SerializeJson(object obj)
        {
            return JsonConvert.SerializeObject(obj, Formatting.None,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        /// <summary>
        /// 将原始响应转换为所需格式：复制 InventoryQtyAdj 行，设置 RowMod
        /// </summary>
        private JObject TransformInventoryQtyAdjResponse(JObject rawResponse)
        {
            var ds = rawResponse["parameters"]?["ds"] as JObject;
            if (ds == null)
                throw new InvalidOperationException("无法从响应中提取 ds 对象");

            var inventoryQtyAdjArray = ds["InventoryQtyAdj"] as JArray;
            if (inventoryQtyAdjArray == null || inventoryQtyAdjArray.Count == 0)
                throw new InvalidOperationException("InventoryQtyAdj 表不存在或为空");

            var originalRow = (JObject)inventoryQtyAdjArray[0].DeepClone();

            var firstRow = (JObject)originalRow.DeepClone();
            var secondRow = (JObject)originalRow.DeepClone();

            firstRow["RowMod"] = "";
            secondRow["RowMod"] = "U";

            var newInventoryQtyAdjArray = new JArray { firstRow, secondRow };

            var newDs = new JObject
            {
                ["InventoryQtyAdj"] = newInventoryQtyAdjArray,
                ["LegalNumGenOpts"] = ds["LegalNumGenOpts"] ?? new JArray(),
                ["SelectedSerialNumbers"] = ds["SelectedSerialNumbers"] ?? new JArray(),
                ["SNFormat"] = ds["SNFormat"] ?? new JArray(),
                ["ExtensionTables"] = ds["ExtensionTables"] ?? new JArray()
            };

            return newDs;
        }

        #endregion
    }
}