using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;
using WarehouseScannerCore.Utilities;

namespace WarehouseScanner.Services
{
    /// <summary>
    /// Epicor 订单发货服务类
    /// 依赖于 EpicorAuthService 获取有效 Token，提供完整的发货流程调用
    /// </summary>
    public class EpicorOutboundService
    {
        private readonly EpicorAuthService _authService;
        private readonly ILogger<EpicorOutboundService>? _logger;

        public EpicorOutboundService(EpicorAuthService authService, string company, ILogger<EpicorOutboundService>? logger = null)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _logger = logger;
        }

        #region 完整发货流程

        /// <summary>
        /// 执行完整的订单发货操作
        /// </summary>
        public async Task<JObject> CreateShipmentAsync(
            string orderNum, string partNum, decimal quantity, string whseCode,
            string ShipViaCode, string? binNum = null, string? lotNum = null,
            int orderLine = 1, int orderRelNum = 2, int packNum = 0, string? revisionNum = null)
        {
            try
            {
                // 1. 根据 packnum 查询出货单据头
                var updateheadResponse = await GetShipmentByPackNumAsync(packNum);
                if (updateheadResponse == null)
                {
                    // GetShipmentByPackNumAsync 把解析/调用异常吞成 null，若直接报"装箱单号不存在"
                    // 会把网络/权限类故障误报为业务不存在，操作员无法据此定位问题
                    if (_authService.HasApiError)
                        throw new InvalidOperationException(_authService.LastApiError ?? "查询装箱单号失败");
                    throw new InvalidOperationException($"装箱单号 {packNum} 不存在");
                }
                if (EpicorJsonHelper.HasError(updateheadResponse))
                    throw new InvalidOperationException("查询发货单头失败");
                var ds = EpicorJsonHelper.ExtractDs(updateheadResponse);

                // 2. 初始化发货明细
                var shipDtlResponse = await GetNewOrdrShipDtlAsync(ds, packNum);
                if (EpicorJsonHelper.HasError(shipDtlResponse))
                    throw new InvalidOperationException("初始化发货明细失败");
                ds = EpicorJsonHelper.ExtractDs(shipDtlResponse);

                // 3. 设置明细基本信息
                ds = SetShipDtlBasicInfo(ds, orderNum, partNum, orderLine, orderRelNum, revisionNum, whseCode, binNum, lotNum, quantity);

                // 4. 获取订单信息
                var orderInfoResponse = await GetOrderInfoAsync(ds, orderNum);
                if (EpicorJsonHelper.HasError(orderInfoResponse))
                    throw new InvalidOperationException("获取订单信息失败");
                ds = EpicorJsonHelper.ExtractDs(orderInfoResponse);

                // 5. 订单号变更校验
                var afterChangedResponse = await AfterChangedShipDtlOrderNumAsync(ds, orderNum, packNum);
                if (EpicorJsonHelper.HasError(afterChangedResponse))
                    throw new InvalidOperationException("订单号变更校验失败");

                // 6. 物料预校验（Epicor BO CheckPrePartInfo 仅接收物料/订单参数，不接收 ds）
                var partInfoResponse = await CheckPrePartInfoAsync(partNum, orderNum, orderLine);
                if (EpicorJsonHelper.HasError(partInfoResponse))
                    throw new InvalidOperationException("物料校验失败");
                // CheckPrePartInfo 的响应通常不含 ds：仅当确实提取到发货数据集时才覆盖，
                // 否则保留步骤 4 的真实 ds，避免把响应壳传给后续步骤导致字段静默丢失。
                var partInfoDs = EpicorJsonHelper.ExtractDs(partInfoResponse);
                if (partInfoDs["ShipHead"] is JArray || partInfoDs["ShipDtl"] is JArray)
                    ds = partInfoDs;

                // 7. 获取订单行信息
                var orderLineResponse = await GetOrderLineInfoAsync(ds, orderLine, 0, partNum);
                if (EpicorJsonHelper.HasError(orderLineResponse))
                    throw new InvalidOperationException("获取订单行信息失败");
                ds = EpicorJsonHelper.ExtractDs(orderLineResponse);

                // 8. 获取订单释放信息
                var orderRelResponse = await GetOrderRelInfoAsync(ds, orderRelNum, 0);
                if (EpicorJsonHelper.HasError(orderRelResponse))
                    throw new InvalidOperationException("获取订单释放信息失败");
                ds = EpicorJsonHelper.ExtractDs(orderRelResponse);

                // 9. 数量校验
                var qtyResponse = await GetQtyInfoAsync(ds, quantity, 0);
                if (EpicorJsonHelper.HasError(qtyResponse))
                    throw new InvalidOperationException("数量校验失败");
                ds = EpicorJsonHelper.ExtractDs(qtyResponse);

                // 10. 库位校验（如果提供了库位）
                if (!string.IsNullOrEmpty(binNum))
                {
                    var binResponse = await CheckPCBinOutLocationAsync(ds);
                    if (EpicorJsonHelper.HasError(binResponse))
                        throw new InvalidOperationException("库位校验失败");
                    ds = EpicorJsonHelper.ExtractDs(binResponse);
                }

                // 11. 处理 ShipHead RowMod
                JObject dataContainer = qtyResponse["returnObj"] is JObject returnObj ? returnObj : qtyResponse;
                if (dataContainer["parameters"]?["ds"] is not JObject dsObj)
                    throw new InvalidOperationException("ds 数据不存在");

                if (!(dsObj["ShipHead"] is JArray shipHeadArray) || shipHeadArray.Count == 0)
                    throw new InvalidOperationException("ShipHead 数据不存在或为空");

                JObject firstShipHead = (JObject)shipHeadArray[0];
                string rowMod = firstShipHead["RowMod"]?.ToString() ?? "";
                if (rowMod != "U")
                {
                    // 原地标记待更新：ShipHead 只需一行 RowMod=U；
                    // 克隆一行再插入会让 BO 收到两行同主键数据。
                    firstShipHead["RowMod"] = "U";

                    JObject result = new JObject { ["parameters"] = new JObject { ["ds"] = dsObj } };
                    ds = EpicorJsonHelper.ExtractDs(result);
                }

                // 12. 再次设置明细基本信息
                ds = SetShipDtlBasicInfo(ds, orderNum, partNum, orderLine, orderRelNum, revisionNum, whseCode, binNum, lotNum, quantity);

                // 13. 保存发货明细
                var firstSaveResponse = await UpdateMasterAsync(ds, packNum, 0);
                if (EpicorJsonHelper.HasError(firstSaveResponse))
                    throw new InvalidOperationException("保存发货明细失败");

                return firstSaveResponse;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "发货流程异常: 订单 {OrderNum}, 物料 {PartNum}", orderNum, partNum);
                throw;
            }
        }

        #endregion

        #region API 步骤方法

        public async Task<JObject> GetNewShipHeadAsync()
        {
            var requestBody = new { ds = CreateEmptyShipDataset() };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/GetNewShipHead", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        public async Task<JObject> UpdateHeadMasterAsync(JObject ds)
        {
            var requestBody = new
            {
                doValidateCreditHold = false, doCheckShipDtl = false, doLotValidation = false,
                doCheckOrderComplete = false, doPostUpdate = false, doCheckCompliance = false,
                ipShippedFlagChanged = false, ipPackNum = 0, ipBTCustNum = 0, ds
            };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/UpdateMaster", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        private async Task<JObject> GetNewOrdrShipDtlAsync(JObject ds, int packNum, int orderNum = 0)
        {
            var requestBody = new { packNum, orderNum, ds };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/GetNewOrdrShipDtl", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        private async Task<JObject> GetOrderInfoAsync(JObject ds, string orderNum)
        {
            var requestBody = new { orderNum, ds };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/GetOrderInfo", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        private async Task<JObject> AfterChangedShipDtlOrderNumAsync(JObject ds, string orderNum, int PackNum)
        {
            var requestBody = new { ipSalesOrder = orderNum, ipPackNum = PackNum, ds };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/AfterChangedShipDtlOrderNum", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        /// <summary>
        /// 物料预校验。Epicor BO CheckPrePartInfo 的请求仅包含 partNum/orderNum/orderLine
        /// （无 ds 形参），故本方法不接收数据集参数，避免误导为"数据集已参与预校验"。
        /// </summary>
        private async Task<JObject> CheckPrePartInfoAsync(string partNum, string orderNum, int orderLine)
        {
            var requestBody = new { partNum, orderNum, orderLine };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/CheckPrePartInfo", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        private async Task<JObject> GetOrderLineInfoAsync(JObject ds, int orderLine, int packLine, string PartNum)
        {
            var requestBody = new { packLine, orderLine, subsPart = PartNum, ds };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/GetOrderLineInfo", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        private async Task<JObject> GetOrderRelInfoAsync(JObject ds, int orderRelNum, int packLine)
        {
            var requestBody = new { packLine, orderRelNum, allowNewShipTo = true, ds };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/GetOrderRelInfo", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        private async Task<JObject> GetQtyInfoAsync(JObject ds, decimal quantity, int packLine)
        {
            var requestBody = new { packLine, displayInvQty = quantity, ourJobShipQty = 0, ds };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/GetQtyInfo", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        /// <summary>
        /// 库位校验：Epicor BO CheckPCBinOutLocation 只接收 ds —— 待校验的库位由调用前
        /// SetShipDtlBasicInfo 写入 ShipDtl.BinNum，故本方法不接收 binNum 形参
        /// （库位已随数据集传给 Epicor，单独形参易被误读为"库位作为参数传给 Epicor"）。
        /// </summary>
        private async Task<JObject> CheckPCBinOutLocationAsync(JObject ds)
        {
            var requestBody = new { ds };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/CheckPCBinOutLocation", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        private async Task<JObject> UpdateMasterAsync(JObject ds, int PackNum, int CustNum)
        {
            var requestBody = new
            {
                doValidateCreditHold = false, doCheckShipDtl = true, doLotValidation = true,
                doCheckOrderComplete = false, doPostUpdate = false, doCheckCompliance = false,
                ipShippedFlagChanged = false, ipPackNum = PackNum, ipBTCustNum = CustNum, ds
            };
            string responseJson = await _authService.PostBoJsonAsync("Erp.BO.CustShipSvc/UpdateMaster", EpicorJsonHelper.Serialize(requestBody));
            return EpicorJsonHelper.ParseResponse(responseJson);
        }

        private async Task<JObject?> GetShipmentByPackNumAsync(int packNum)
        {
            try
            {
                string requestUrl = $"Erp.BO.CustShipSvc/GetByID?packNum={packNum}";
                string responseJson = await _authService.GetBoJsonAsync(requestUrl);
                return string.IsNullOrEmpty(responseJson) ? null : JObject.Parse(responseJson);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "获取发货数据失败，PackNum: {PackNum}", packNum);
                _authService.SetApiError("查询装箱单失败，请稍后重试");
                return null;
            }
        }

        #endregion

        #region 辅助方法

        private JObject SetShipDtlBasicInfo(JObject ds, string orderNum, string partNum,
            int orderLine, int orderRelNum, string? revisionNum, string whseCode,
            string? binNum, string? lotNum, decimal quantity)
        {
            var shipDtlArray = ds["ShipDtl"] as JArray;
            if (shipDtlArray == null || shipDtlArray.Count == 0) return ds;

            foreach (var item in shipDtlArray.OfType<JObject>())
            {
                item["OrderNum"] = orderNum;
                item["PartNum"] = partNum;
                item["OrderLine"] = orderLine;
                item["OrderRelNum"] = orderRelNum;
                if (!string.IsNullOrEmpty(revisionNum)) item["RevisionNum"] = revisionNum;
                item["WarehouseCode"] = whseCode;
                item["BinNum"] = binNum ?? "";
                item["BinNumDescription"] = binNum ?? "";
                if (!string.IsNullOrEmpty(lotNum)) item["LotNum"] = lotNum;
                item["OurInventoryShipQty"] = quantity;
                item["RowMod"] = "A";
            }
            return ds;
        }

        public JObject ExtractDs(JObject response) => EpicorJsonHelper.ExtractDs(response);

        public int ExtractPackNum(JObject ds)
        {
            try
            {
                var shipHeadArray = ds["ShipHead"] as JArray;
                return shipHeadArray?.Count > 0 ? shipHeadArray[0]["PackNum"]?.Value<int>() ?? 0 : 0;
            }
            // 静默返回 0 会让上层把"解析失败"当"无数据"处理（与 GeneratePackId 的 packNum=0 放行叠加），
            //     至少补日志留下可观测性
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "解析 PackNum 失败（ShipHead 缺失或格式异常），返回 0");
                return 0;
            }
        }

        private static object CreateEmptyShipDataset() => new
        {
            LegalNumberGenerate = new object[] { }, MXShipHeadCustRegime = new object[] { },
            ReplicatedPacks = new object[] { }, ShipDtlPackaging = new object[] { },
            ShipHeadTrailer = new object[] { }, CartonTrkDtl = new object[] { },
            CartonTrkDtlContent = new object[] { }, LegalNumGenOpts = new object[] { },
            SalesKitCompIssue = new object[] { }, SelectedIDNumbers = new object[] { },
            SelectedSerialNumbers = new object[] { }, ShipCOO = new object[] { },
            ShipDtl = new object[] { }, ShipDtlAttch = new object[] { },
            ShipDtlTax = new object[] { }, ShipHead = new object[] { },
            ShipHeadAttch = new object[] { }, ShipHeadInsurance = new object[] { },
            ShipMisc = new object[] { }, ShipTaxSum = new object[] { },
            ShipUPS = new object[] { }, SNFormat = new object[] { }
        };

        #endregion
    }
}
