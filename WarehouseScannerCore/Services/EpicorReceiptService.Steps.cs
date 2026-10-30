using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>API 步骤方法</summary>
    public partial class EpicorReceiptService
    {
        #region API 步骤方法

        /// <summary>
        /// 解析步骤响应：空/非法/错误响应不抛异常，而是返回带 ErrorMessage 的对象，
        /// 由 ExecuteReceiptAsync 统一包装为"步骤N …失败: 原因"的用户可见文案。
        /// </summary>
        private JObject ParseStepResponse(string responseJson, string stepName)
        {
            var (obj, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
            if (hasError)
            {
                _logger?.LogWarning("{Step} 响应无效: {Error}", stepName, errorMsg);
                return new JObject { ["ErrorMessage"] = errorMsg };
            }
            return obj;
        }

        /// <summary>
        /// 步骤1: GetNewReceiptsFromMfgJobAsm - 获取新的收货数据集
        /// </summary>
        public async Task<JObject> GetNewReceiptsFromMfgJobAsmAsync(
            string pcJobNum, int piAssemblySeq, string pcTranType = "MFG-STK",
            string pcProcessID = "JobReceiptToInventory")
        {
            var requestBody = new
            {
                pcJobNum = pcJobNum,
                piAssemblySeq = piAssemblySeq,
                pcTranType = pcTranType,
                pcProcessID = pcProcessID,
                ds = new
                {
                    ReceiptsFromMfg = new[]
                    {
                        new
                        {
                            JobNum = pcJobNum,
                            AssemblySeq = piAssemblySeq,
                            TranType = pcTranType,
                            ProcessID = pcProcessID,
                            PartNum = string.Empty,
                            Quantity = 0,
                            WarehouseCode = string.Empty,
                            BinNum = string.Empty,
                            LotNum = string.Empty,
                            SerialNum = string.Empty,
                            RowMod = "A"
                        }
                    }
                }
            };

            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.ReceiptsFromMfgSvc/GetNewReceiptsFromMfgJobAsm",
                EpicorJsonHelper.Serialize(requestBody));
            return ParseStepResponse(responseJson, "步骤1 GetNewReceiptsFromMfgJobAsm");
        }

        /// <summary>
        /// 步骤2: OnChangeActTranQty - 触发 Epicor BO 数量变更校验（获取 pcMessage 警告）
        /// 数量字段已由 SetPartTranData 设置，此处仅标记 RowMod 并调用 BO。
        /// </summary>
        public async Task<JObject> OnChangeActTranQtyAsync(JObject ds)
        {
            // 标记 PartTran 行（数量字段已由 SetPartTranData 设置，无需重复赋值）
            if (ds?["PartTran"] is JArray partTranArray && partTranArray.Count > 0)
            {
                foreach (var item in partTranArray.OfType<JObject>())
                {
                    item["RowMod"] = "U";
                }
            }

            // 标记 JobReceiptDemand 行
            if (ds?["JobReceiptDemand"] is JArray demandArray && demandArray.Count > 0)
            {
                foreach (var item in demandArray.OfType<JObject>())
                {
                    item["RowMod"] = "U";
                }
            }

            var requestBody = new { ds };
            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.ReceiptsFromMfgSvc/OnChangeActTranQty",
                EpicorJsonHelper.Serialize(requestBody));
            return ParseStepResponse(responseJson, "步骤2 OnChangeActTranQty");
        }

        /// <summary>
        /// 步骤3: ChkForNeedsLotAttrs - 检查批次属性需求
        /// </summary>
        public async Task<JObject> ChkForNeedsLotAttrsAsync(string pcPartNum, string pcLotNum, string pcDeferOption = "None")
        {
            var requestBody = new
            {
                pcPartNum = pcPartNum,
                pcLotNum = pcLotNum,
                pcDeferOption = pcDeferOption
            };

            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.LotSelectUpdateSvc/ChkForNeedsLotAttrs",
                EpicorJsonHelper.Serialize(requestBody));
            return ParseStepResponse(responseJson, "步骤3 ChkForNeedsLotAttrs");
        }

        /// <summary>
        /// 步骤4: PreUpdate - 预更新数据集
        /// </summary>
        public async Task<JObject> PreUpdateAsync(JObject ds, string jobNum, string lotNum, double quantity, string? whseCode = null)
        {
            // 更新 PartTran 中的关键字段
            if (ds?["PartTran"] is JArray partTranArray && partTranArray.Count > 0)
            {
                foreach (var item in partTranArray.OfType<JObject>())
                {
                    item["TranQty"] = quantity;
                    item["ThisTranQty"] = quantity;
                    item["ActTranQty"] = quantity;
                    item["LotNum"] = lotNum ?? jobNum;
                    item["RowMod"] = "A";

                    if (!string.IsNullOrEmpty(whseCode))
                        item["WareHouseCode"] = whseCode;

                    // 设置 EntryPerson
                    if (!string.IsNullOrEmpty(_operatorId))
                        item["EntryPerson"] = _operatorId;
                }
            }

            var requestBody = new { ds };
            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.ReceiptsFromMfgSvc/PreUpdate",
                EpicorJsonHelper.Serialize(requestBody));
            return ParseStepResponse(responseJson, "步骤4 PreUpdate");
        }

        /// <summary>
        /// 步骤5: VerifySerialMatchAndPlanContract - 验证序列号匹配
        /// </summary>
        public async Task<JObject> VerifySerialMatchAndPlanContractAsync(JObject ds)
        {
            var requestBody = new { ds };
            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.ReceiptsFromMfgSvc/VerifySerialMatchAndPlanContract",
                EpicorJsonHelper.Serialize(requestBody));
            return ParseStepResponse(responseJson, "步骤5 VerifySerialMatchAndPlanContract");
        }

        /// <summary>
        /// 步骤6: ReceiveMfgPartToInventory - 执行入库到库存
        /// </summary>
        public async Task<JObject> ReceiveMfgPartToInventoryAsync(
            JObject ds, double serialNoQty = 0, bool negQtyAction = false,
            string pcProcessID = "JobReceiptToInventory")
        {
            var requestBody = new
            {
                pdSerialNoQty = serialNoQty,
                plNegQtyAction = negQtyAction,
                pcProcessID = pcProcessID,
                ds = ds
            };

            string responseJson = await _authService.PostBoJsonAsync(
                "Erp.BO.ReceiptsFromMfgSvc/ReceiveMfgPartToInventory",
                EpicorJsonHelper.Serialize(requestBody));
            return ParseStepResponse(responseJson, "步骤6 ReceiveMfgPartToInventory");
        }

        #endregion
    }
}