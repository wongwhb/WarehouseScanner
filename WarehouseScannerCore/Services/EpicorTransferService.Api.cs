﻿﻿﻿﻿﻿﻿﻿using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>私有 API 调用方法</summary>
    public partial class EpicorTransferService
    {
        #region 私有API调用方法

        /// <summary>
        /// 获取指定物料的库存调整数据集（初始化调整单据）
        /// </summary>
        /// <param name="partNum">物料号</param>
        /// <param name="warehouseCode">仓库代码</param>
        /// <param name="binNum">库位号（可为空）</param>
        /// <param name="lotNum">批次号（可为空）</param>
        /// <returns>包含 InventoryQtyAdj 数据集的 JObject</returns>
        private async Task<JObject> GetInventoryQtyAdjForPartAsync(string partNum, string warehouseCode, string binNum, string lotNum)
        {
            var requestBody = new
            {
                partnumber = partNum,           // 字段名改为全小写 partnumber
                uomCode = "",                    // 单位设为空字符串
                wareHseCode = warehouseCode,
                binNum = binNum ?? "",
                lotNum = lotNum ?? "",
                ds = new
                {
                    InventoryQtyAdj = new object[] { }   // 空数组
                }
            };
            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InventoryQtyAdjSvc/GetInventoryQtyAdjForPart", jsonPayload);
            var response = ParseResponse(responseJson);
            // 转换为目标格式
            var transformedDs = TransformInventoryQtyAdjResponse(response);
            return transformedDs;
        }

        /// <summary>
        /// 验证物料编号在库存转移事务中是否有效
        /// </summary>
        /// <param name="ds">当前库存转移数据集</param>
        /// <param name="proposedPartNum">待验证的物料号</param>
        /// <param name="uomCodePartXRef">单位代码（可选）</param>
        /// <param name="refreshMode">是否刷新模式</param>
        /// <param name="partList">物料列表（可选）</param>
        /// <returns>验证后更新过的数据集 JObject</returns>
        private async Task<JObject> ValidatePartNumAsync(JObject ds, string proposedPartNum, string uomCodePartXRef = "", bool refreshMode = false, string partList = "")
        {
            var requestBody = new
            {
                proposedPartNum = proposedPartNum,
                uomCodePartXRef = uomCodePartXRef,
                refreshMode = refreshMode,
                partList = partList,
                ds = ds
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InvTransferSvc/ValidatePartNum", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 修改库存转移的源库位（触发相关验证和计算）
        /// </summary>
        /// <param name="ds">当前库存转移数据集</param>
        /// <param name="ipBinNum">源库位号</param>
        /// <returns>更新后的数据集 JObject</returns>
        private async Task<JObject> ChangeFromBinRowModAsync(JObject ds, string ipBinNum)
        {
            var requestBody = new
            {
                ipBinNum = ipBinNum,
                ds = ds
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InvTransferSvc/ChangeFromBinRowMod", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 修改库存转移的目标库位（触发相关验证和计算）
        /// </summary>
        /// <param name="ds">当前库存转移数据集</param>
        /// <param name="ipToBinNum">目标库位号</param>
        /// <returns>更新后的数据集 JObject</returns>
        private async Task<JObject> ChangeToBinRowModAsync(JObject ds, string ipToBinNum)
        {
            var requestBody = new
            {
                ipToBinNum = ipToBinNum,   // 注意参数名改为 ipToBinNum，与示例一致
                ds = ds
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InvTransferSvc/ChangeToBinRowMod", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 修改库存转移数量
        /// </summary>
        /// <param name="ds">当前库存转移数据集</param>
        /// <param name="proposedValue">新的转移数量（字符串形式）</param>
        /// <returns>更新后的数据集 JObject</returns>
        private async Task<JObject> ChangeTransferQtyRowModAsync(JObject ds, string proposedValue)
        {
            var requestBody = new
            {
                proposedValue = proposedValue,   // 与请求示例中的字段名一致
                ds = ds
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InvTransferSvc/ChangeTransferQtyRowMod", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 修改库存转移的批次号（源或目标）
        /// </summary>
        /// <param name="ds">当前库存转移数据集</param>
        /// <param name="ipLotNum">批次号</param>
        /// <param name="ipFromTo">指定修改源批次 ("From") 或目标批次 ("To")</param>
        /// <returns>更新后的数据集 JObject</returns>
        private async Task<JObject> ChangeLotRowModAsync(JObject ds, string ipLotNum, string ipFromTo)
        {
            var requestBody = new
            {
                ipLotNum = ipLotNum,      // 批次号
                ipFromTo = ipFromTo,      // "From" 或 "To"，表示修改源批次或目标批次
                ds = ds
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InvTransferSvc/ChangeLotRowMod", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 检查指定物料和批次是否需要录入批次属性（如到期日期、质量状态等）
        /// </summary>
        /// <param name="pcPartNum">物料号</param>
        /// <param name="pcLotNum">批次号</param>
        /// <param name="pcDeferOption">延迟选项（例如 "Inventory Move"）</param>
        /// <returns>包含检查结果的数据集 JObject</returns>
        private async Task<JObject> ChkForNeedsLotAttrsAsync(string pcPartNum, string pcLotNum, string pcDeferOption)
        {
            var requestBody = new
            {
                pcPartNum = pcPartNum,
                pcLotNum = pcLotNum,
                pcDeferOption = pcDeferOption
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.LotSelectUpdateSvc/ChkForNeedsLotAttrs", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 执行主库存测试，验证库存转移操作前的业务规则（如库位有效性、批次等）
        /// </summary>
        /// <param name="ds">当前库存转移数据集</param>
        /// <returns>测试后更新（或验证通过）的数据集 JObject</returns>
        private async Task<JObject> MasterInventoryBinTestsAsync(JObject ds)
        {
            var requestBody = new
            {
                ds = ds   // 请求体中只包含 ds 字段，与示例一致
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InvTransferSvc/MasterInventoryBinTests", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 预提交库存转移事务，进行最终验证和准备（检查负库存、法定编号要求等）
        /// </summary>
        /// <param name="ds">当前库存转移数据集</param>
        /// <returns>预提交后更新（或验证通过）的数据集 JObject</returns>
        private async Task<JObject> PreCommitTransferAsync(JObject ds)
        {
            var requestBody = new
            {
                ds = ds   // 请求体中只包含 ds 字段，与示例一致
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InvTransferSvc/PreCommitTransfer", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 提交库存转移事务并更新历史记录
        /// </summary>
        /// <param name="ds">包含最终确认的转移数据的 JObject</param>
        /// <returns>提交后返回的数据集 JObject（可能包含更新后的历史记录）</returns>
        private async Task<JObject> CommitTransferAndUpdateHistoryAsync(JObject ds)
        {
            var requestBody = new
            {
                ds = ds   // 请求体中只包含 ds 字段，与示例一致
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.InvTransferSvc/CommitTransferAndUpdateHistory", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        /// <summary>
        /// 批号更新前初始化
        /// </summary>
        private async Task<JObject> GetNewPartLotAsync( string partNum)
        {
            // 构建请求体，包含批次号和当前数据集

            JObject ds = new JObject
            {
                ["PartLot"] = new JArray(),
                ["PartLotAttch"] = new JArray(),
                ["ExtensionTables"] = new JArray()
            };
            var requestBody = new
            {
                partNum = partNum,   // 物料号
                ds =ds           // 当前数据集，包含 PartLot、PartLotAttch、ExtensionTables 等
            };

            // 序列化为 JSON 字符串
            string jsonPayload = SerializeJson(requestBody);

            // 调用 POST 方法，指定服务路径为 "Erp.BO.LotSelectUpdateSvc/GetNewPartLot"
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.LotSelectUpdateSvc/GetNewPartLot", jsonPayload);

            // 解析响应 JSON
            var response = ParseResponse(responseJson);

            // 提取返回的 ds 部分（通常服务会返回更新后的数据集）
            return EpicorJsonHelper.ExtractDs(response);
        }
        /// <summary>
        /// 更新批号
        /// </summary>

        private async Task<JObject> CommitUpdatePartLotAsync(JObject ds)
        {
            var requestBody = new
            {
                ds = ds   
            };

            string jsonPayload = SerializeJson(requestBody);
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.LotSelectUpdateSvc/Update", jsonPayload);
            var response = ParseResponse(responseJson);
            return EpicorJsonHelper.ExtractDs(response);
        }

        #endregion
    }
}