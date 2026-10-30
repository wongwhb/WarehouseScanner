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
    /// <summary>Epicor API 辅助方法</summary>
    public partial class ReceivinController
    {
        #region 辅助方法

        /// <summary>
        /// 在 ds 中设置 PackSlip 值
        /// </summary>
        private void SetPackSlipInDs(JObject? ds, string packSlip)
        {
            if (ds == null || string.IsNullOrEmpty(packSlip)) return;

            var rcvHeadArray = ds["RcvHead"] as JArray;
            if (rcvHeadArray != null && rcvHeadArray.Any())
            {
                var first = rcvHeadArray.First() as JObject;
                if (first != null)
                {
                    first["PackSlip"] = packSlip;
                }
            }
        }

        /// <summary>
        /// 在 ds 的 RcvDtl 数组中，根据 PONum + POLine 查找匹配记录（优先新增行 RowMod="A"）。
        /// 无法唯一定位时返回 null，绝不回退"任意新增行"或"第一条"。
        /// </summary>
        private JObject? FindRcvDtlRecord(JObject ds, string poNum, int poLine)
        {
            if (ds == null) return null;

            var rcvDtlArray = ds["RcvDtl"] as JArray;
            if (rcvDtlArray == null || !rcvDtlArray.Any()) return null;

            // 安全读取整数值：兼容整数、数字字符串与空值，不抛异常。
            // 不能直接用 Value<int>() 解析 PONum/POLine：空串或非数字串会抛 FormatException，
            // 异常从 FirstOrDefault 谓词内直接上抛，会中断整条收货提交链路。
            static bool TryReadInt(JToken? token, out int value)
            {
                value = 0;
                if (token == null || token.Type == JTokenType.Null) return false;
                if (token.Type == JTokenType.Integer)
                {
                    value = token.Value<int>();
                    return true;
                }
                return int.TryParse(token.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }

            string poNumText = (poNum ?? string.Empty).Trim();

            bool IsSamePoLine(JObject r)
                => string.Equals(r["PONum"]?.ToString().Trim(), poNumText, StringComparison.OrdinalIgnoreCase)
                && TryReadInt(r["POLine"], out int line) && line == poLine;

            // 优先匹配 PONum + POLine + RowMod="A"（新增记录）
            var matched = rcvDtlArray
                .OfType<JObject>()
                .FirstOrDefault(r => IsSamePoLine(r) && r["RowMod"]?.ToString() == "A");

            if (matched != null) return matched;

            // 回退：匹配 PONum + POLine（不限 RowMod，仍然按业务键唯一定位）
            matched = rcvDtlArray
                .OfType<JObject>()
                .FirstOrDefault(IsSamePoLine);

            if (matched != null) return matched;

            // 定位失败不猜测：禁止"回退到 RowMod=A 的任意行"乃至"第一条"，
            // 否则 dtlInfoDs 含多行 RcvDtl 时会把批号/数量/仓库库位写到错误的 PO 行（静默错单）。
            // 返回 null，由调用方保持"字段未写入"，让后续 Epicor 步骤报错中止本次提交。
            _logger.LogError("未能在 RcvDtl 中定位记录: PONum={PoNum}, POLine={PoLine}, RcvDtl 行数={RowCount}",
                poNum, poLine, rcvDtlArray.Count);
            return null;
        }

        /// <summary>
        /// 在 ds 中设置 OurQty 和 InputOurQty 值
        /// </summary>
        private void SetOurQtyInDs(JObject ds, string poNum, int poLine, decimal qty)
        {
            if (ds == null || qty <= 0) return;

            var record = FindRcvDtlRecord(ds, poNum, poLine);
            if (record != null)
            {
                record["OurQty"] = qty;
                record["InputOurQty"] = qty;
            }
        }

        /// <summary>
        /// 在 ds 中设置 LotNum 值
        /// </summary>
        private void SetLotNumInDs(JObject ds, string poNum, int poLine, string lotNum)
        {
            if (ds == null || string.IsNullOrEmpty(lotNum)) return;

            var record = FindRcvDtlRecord(ds, poNum, poLine);
            if (record != null)
            {
                record["LotNum"] = lotNum;
            }
        }

        /// <summary>
        /// 在 ds 中设置 PORelNum 值
        /// </summary>
        private void SetPORelNumInDs(JObject ds, string poNum, int poLine, int poRelNum)
        {
            if (ds == null || poRelNum <= 0) return;

            var record = FindRcvDtlRecord(ds, poNum, poLine);
            if (record != null)
            {
                record["PORelNum"] = poRelNum;
              

            }
        }

        /// <summary>
        /// 在 ds 中设置 WareHouseCode 和 BinNum 值
        /// </summary>
        private void SetWarehouseBinInDs(JObject ds, string poNum, int poLine, string warehouseCode, string binNum)
        {
            if (ds == null) return;

            var record = FindRcvDtlRecord(ds, poNum, poLine);
            if (record != null)
            {
                if (!string.IsNullOrEmpty(warehouseCode))
                {
                    record["WareHouseCode"] = warehouseCode;
                }
                if (!string.IsNullOrEmpty(binNum))
                {
                    record["BinNum"] = binNum;
                }
            }
        }

        /// <summary>
        /// 在 ds 中设置 ReceivedComplete、VendorQty、Received 等字段。
        /// 权威来源说明：ds 中的 "Received" 字段是 Epicor BO 内部状态标记，
        /// 用于 OnChangeDtlReceived 计算衍生值；真正持久化收货由 OnChangeDtlReceivedAsync
        /// 的 ipReceived="true" 触发。两者语义不同，不能混为一谈：
        ///   - ds.Received=false：表示当前 UI 行尚未"打勾确认"，让 BO 走 OnChange 计算流程
        ///   - ipReceived="true"：表示本次调用是"用户打勾"动作本身
        /// 因此 ds.Received=false 与 ipReceived=true 是配合关系，不是矛盾。
        /// </summary>
        private void SetReceivedCompleteInDs(JObject ds, string poNum, int poLine, bool receivedComplete, decimal qty)
        {
            if (ds == null) return;

            var record = FindRcvDtlRecord(ds, poNum, poLine);
            if (record != null)
            {
                record["ReceivedComplete"] = receivedComplete;
                record["VendorQty"] = qty;
                // 置 false 让 OnChangeDtlReceived 走"用户打勾"衍生计算流程，
                // 真正的收货持久化由 UpdateMaster 在 Step 12 完成。
                record["Received"] = false;
            }
        }

        #endregion
    }
}