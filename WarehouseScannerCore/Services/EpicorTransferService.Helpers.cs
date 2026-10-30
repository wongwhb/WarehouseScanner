using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace WarehouseScanner.Services
{
    /// <summary>辅助方法</summary>
    public partial class EpicorTransferService
    {
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
            // 1. 从原始响应的 parameters.ds 中提取数据
            var ds = rawResponse["parameters"]?["ds"] as JObject;
            if (ds == null)
                throw new InvalidOperationException("无法从响应中提取 ds 对象");

            // 2. 读取 InventoryQtyAdj 表
            var inventoryQtyAdjArray = ds["InventoryQtyAdj"] as JArray;
            if (inventoryQtyAdjArray == null || inventoryQtyAdjArray.Count == 0)
                throw new InvalidOperationException("InventoryQtyAdj 表不存在或为空");

            // 3. 深拷贝第一个元素（假设我们取第一行作为基准）
            var originalRow = (JObject)inventoryQtyAdjArray[0].DeepClone();

            // 4. 创建两个副本
            var firstRow = (JObject)originalRow.DeepClone();
            var secondRow = (JObject)originalRow.DeepClone();

            // 5. 设置 RowMod（第一行空，第二行 "U"）
            firstRow["RowMod"] = "";
            secondRow["RowMod"] = "U";

            // 6. 构建新的 InventoryQtyAdj 数组
            var newInventoryQtyAdjArray = new JArray { firstRow, secondRow };

            // 7. 构建新的 ds 对象，保留其他表（保持原样或空数组）
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