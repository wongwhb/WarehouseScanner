using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>私有调整方法</summary>
    public partial class EpicorTransferService
    {
        #region 私有调整方法

        /// <summary>
        /// 对单个位置执行库存调整（增加或减少）
        /// </summary>
        private async Task<JObject> AdjustInventoryAsync(
            string partNum,
            decimal adjustQty,
            string fromWarehouse,
            string fromBinNum,
            string fromLotNum,
             string toWarehouse,
             string toBinNum,
             string toLotNum,
             bool isToBatchNo,

            string? remark)
        {
            // 转移原因写入服务端日志：ERP 侧无原因字段（InvTrans 未写入自由文本），
            // 由应用侧日志承担审计追溯；换行会破坏按行日志，先清洗成单行。
            if (!string.IsNullOrWhiteSpace(remark))
            {
                _logger?.LogInformation("库存转移 物料 {PartNum}, 数量 {Quantity}, 自 {FromWhse}/{FromBin}/{FromLot} 至 {ToWhse}/{ToBin}/{ToLot}, 原因 {Remark}",
                    partNum, adjustQty, fromWarehouse, fromBinNum, fromLotNum, toWarehouse, toBinNum, toLotNum,
                    remark.Replace('\r', ' ').Replace('\n', ' ').Trim());
            }


            // 1. 获取调整数据集
            var AdjForPartResponse = await GetInventoryQtyAdjForPartAsync(partNum, fromWarehouse, fromBinNum, fromLotNum);
            if (AdjForPartResponse == null)
                throw new InvalidOperationException("初始化调整单据失败");
            var ds = EpicorJsonHelper.ExtractDs(AdjForPartResponse);

            // 2. 验证零件编号
            var validatePartNumResponse = await ValidatePartNumAsync(ds, partNum);
            if (validatePartNumResponse == null)
                throw new InvalidOperationException("验证零件编号失败");
            ds = validatePartNumResponse;   // ValidatePartNumAsync 已返回 ds JObject


            // 说明（待业务确认）：此 ds 来自 InventoryQtyAdjSvc/GetInventoryQtyAdjForPart，
            // 经 TransformInventoryQtyAdjResponse 后只含 InventoryQtyAdj / LegalNumGenOpts 等键，
            // 不含 InvTransferSvc 期望的 InvTrans 表。原先在此修改 ds["InvTrans"] 的 RowMod
            // 属于永远不可达的死代码（下一步 ValidatePartNum 的返回 ds 才包含 InvTrans），已移除。
            // 若业务确实需要设置 InvTrans.RowMod，应改为在 ValidatePartNum/ChangeFromBinRowMod
            // 等 InvTransferSvc 调用返回后再修改；该业务意图需与 Epicor 顾问确认，避免猜测写入结构。

            // 3. 更改源库位
            var changeFromBinResponse = await ChangeFromBinRowModAsync(ds, fromBinNum);
            if (changeFromBinResponse == null)
                throw new InvalidOperationException("更改源库位失败");
            ds = changeFromBinResponse;

            // 4. 更改目标库位
            var changeToBinResponse = await ChangeToBinRowModAsync(ds, toBinNum);
            if (changeToBinResponse == null)
                throw new InvalidOperationException("更改目标库位失败");
            ds = changeToBinResponse;

            // 5. 更改转移数量
            // 数字转字符串必须用 InvariantCulture，避免中文/欧洲区域下小数点变成逗号传给 Epicor
            var changeQtyResponse = await ChangeTransferQtyRowModAsync(ds, adjustQty.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (changeQtyResponse == null)
                throw new InvalidOperationException("更改转移数量失败");
            ds = changeQtyResponse;

            // 6. 更改源批次
            var changefromLotResponse = await ChangeLotRowModAsync(ds, fromLotNum, "From");
            if (changefromLotResponse == null)
                throw new InvalidOperationException("更改批次失败");
            ds = changefromLotResponse;

            // 初始化批次：批次在系统中不存在时，按手工指定的目标批次号创建 PartLot。
            // 注意：isToBatchNo 的语义是「目标批号是否已存在」（见方法参数注释），
            //       因此这里必须取反——批次不存在（false）才需要创建。
            //       警示：禁止把赋值误用作比较（if (isToBatchNo = false) 会使条件恒为 false，
            //       整块创建逻辑成为死代码）。
            if (!isToBatchNo)
            {
                var PartLotResponse = await GetNewPartLotAsync(partNum);
                if (PartLotResponse == null)
                    throw new InvalidOperationException("初始化批次失败");

                // 说明：此处使用的是 LotSelectUpdateSvc 的 PartLot 数据集（ds2），
                //       与主流程的 ds（InventoryQtyAdj BO）不是同一个业务对象，
                //       故提交后不回写主流程 ds，避免跨 BO 污染数据集。
                var ds2 = PartLotResponse;
                // 新建 PartLot 的 RowMod 必须为 "A"（Add），Epicor 才会执行新增
                JToken? PartLotToken = ds2["PartLot"];
                if (PartLotToken != null && PartLotToken.Type == JTokenType.Array)
                {
                    JArray PartLotArray = (JArray)PartLotToken;
                    if (PartLotArray.Count >= 1)
                    {
                        ((JObject)PartLotArray[0])["RowMod"] = "A";
                        ((JObject)PartLotArray[0])["LotNum"] = toLotNum;
                    }
                }

                var UpdatePartLotResponse = await CommitUpdatePartLotAsync(ds2);
                if (UpdatePartLotResponse == null)
                    throw new InvalidOperationException("更改批次失败");
            }

            // 7. 更改目标批次
            var changeLotResponse = await ChangeLotRowModAsync(ds, toLotNum, "To");
            if (changeLotResponse == null)
                throw new InvalidOperationException("更改批次失败");
            ds = changeLotResponse;

         

            // 8. 检查是否需要批次属性
            var chkNeedsResponse = await ChkForNeedsLotAttrsAsync(partNum, fromLotNum, "Inventory Move");
            if (chkNeedsResponse == null)   // 即使不提取 ds，也需确保调用成功
                throw new InvalidOperationException("检查批次属性失败");
            // 此处无需更新 ds，该方法返回的可能不是 ds 数据集

            // 9. 执行主库存测试
            var masterTestResponse = await MasterInventoryBinTestsAsync(ds);
            if (masterTestResponse == null)
                throw new InvalidOperationException("主库存测试失败");
            ds = masterTestResponse;

            // 10. 预提交转移
            var preCommitResponse = await PreCommitTransferAsync(ds);
            if (preCommitResponse == null)
                throw new InvalidOperationException("预提交转移失败");
            ds = preCommitResponse;
            //11.执行库存转移并更新交易历史
            var result = await CommitTransferAndUpdateHistoryAsync(ds);



            return result;
        }

        #endregion
    }
}