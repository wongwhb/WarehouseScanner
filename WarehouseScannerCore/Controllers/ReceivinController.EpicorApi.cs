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
    /// <summary>Epicor API 调用方法</summary>
    public partial class ReceivinController
    {
        #region Epicor API 调用方法

        private async Task<JObject?> GetNewRcvHeadAsync(EpicorAuthService authService)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/GetNewRcvHead";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum = 0,
                    purPoint = "",
                    ds = new { }
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("GetNewRcvHead 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetNewRcvHead 调用失败");
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// 通过 GetByID 获取已存在的收货头数据集。
        /// 返回 (response, callFailed)：
        ///   - Epicor 业务错误（如收货头不存在）→ (null, false)：上层按"无收货头 → 创建新头"继续（原语义）；
        ///   - 网络/服务异常 → (null, true)：上层应终止流程。
        /// 禁止把所有异常吞为 null 且不设 ApiError：网络瞬断会被当作
        ///     "收货头不存在"而走创建新头分支，存在同一送货单重复建头风险。
        /// </summary>
        private async Task<(JObject? response, bool callFailed)> GetByIDAsync(EpicorAuthService authService, int vendorNum, string purPoint, string packSlip)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/GetByID";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum,
                    purPoint,
                    packSlip
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogWarning("GetByID 返回错误（可能收货头不存在，将创建新头）: {ErrorMsg}", errorMsg);
                    return (null, false);
                }
                return (response, false);
            }
            catch (Exception ex)
            {
                // 异常（网络/认证/解析崩溃）与"收货头不存在"语义不同，
                //     标记 callFailed 让上层 Fail 终止，避免误走创建新头分支
                _logger.LogError(ex, "GetByID 调用失败（网络或服务异常，终止收货流程）: PackSlip={PackSlip}", packSlip);
                return (null, true);
            }
        }

        private async Task<JObject?> CNPreUpdateAsync(EpicorAuthService authService, JObject? ds)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/CNPreUpdate";
                var payload = EpicorJsonHelper.Serialize(new { ds });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("CNPreUpdate 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CNPreUpdate 调用失败");
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        private async Task<JObject?> UpdateMasterAsync(EpicorAuthService authService, JObject ds, int vendorNum, string packSlip)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/UpdateMaster";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    RunChkLCAmtBeforeUpdate = true,
                    RunChkHdrBeforeUpdate = true,
                    ipVendorNum = vendorNum,
                    ipPurPoint = "",
                    ipPackSlip = packSlip,
                    ipPackLine = "0",
                    lRunChkDtl = true,
                    lRunChkDtlCompliance = true,
                    lRunCheckCompliance = false,
                    lRunPreUpdate = true,
                    lRunCreatePartLot = false,
                    partNum = "",
                    lotNum = "",
                    lOkToUpdate = true,
                    ds
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("UpdateMaster 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateMaster 调用失败");
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// GetPOInfo 方法（使用指定的 ds）
        /// </summary>
        private async Task<POInfoResult?> GetPOInfoWithDsAsync(EpicorAuthService authService, string poNum, JObject ds)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/GetPOInfo";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    poNum,
                    fromReceiptEntryNewRcpt = true,
                    ds
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);


                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);

                if (hasError)
                {
                    _logger.LogError("GetPOInfo 响应错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }

                // Epicor BO 可能返回 returnObj 表示失败
                var returnObj = response?.SelectToken("returnObj");
                if (returnObj != null && returnObj.ToObject<bool>() == false)
                {
                    _logger.LogWarning("GetPOInfo 返回 false: PONum={PONum}", poNum);
                    authService.SetApiError(GetText("Receivin", "GetPOInfoReturnedFalse"));
                    return null;
                }

                var headToken = response?.SelectToken("parameters.ds.RcvHead")?.FirstOrDefault();
                if (headToken == null)
                {
                    authService.SetApiError(GetText("Receivin", "GetPOInfoNoRcvHead"));
                    return null;
                }

                var result = new POInfoResult
                {
                    PONum = headToken.SelectToken("PONum")?.ToString() ?? poNum,
                    VendorNum = headToken.SelectToken("VendorNum")?.ToObject<int>() ?? 0,
                    VendorName = headToken.SelectToken("VendorNumName")?.ToString() ?? string.Empty,
                    POType = headToken.SelectToken("POTypeDesc")?.ToString() ?? string.Empty,
                    POStatus = headToken.SelectToken("PONumApprovalStatus")?.ToString() ?? string.Empty,
                    TotalAmt = headToken.SelectToken("TotalAmt")?.ToObject<decimal>() ?? 0,
                    ShipViaCode = headToken.SelectToken("ShipViaCodeDescription")?.ToString() ?? string.Empty,
                    TaxRegionCode = headToken.SelectToken("TaxRegionCodeDescription")?.ToString() ?? string.Empty,
                    CurrencyCode = headToken.SelectToken("CurrencyCode")?.ToString() ?? string.Empty,
                    PODueDate = headToken.SelectToken("PODueDate")?.ToString() ?? string.Empty,
                    RawDs = response?["parameters"]?["ds"] as JObject
                };

                // 解析明细行项目
                var itemsToken = response?.SelectToken("parameters.ds.RcvDtl");
                if (itemsToken != null)
                {
                    result.Items = itemsToken.Select(i => new POInfoItem
                    {
                        POLine = i.SelectToken("POLine")?.ToObject<int>() ?? 0,
                        PartNum = i.SelectToken("PartNum")?.ToString() ?? string.Empty,
                        PartDescription = i.SelectToken("PartDescription")?.ToString() ?? string.Empty,
                        OrderQty = i.SelectToken("OrderQty")?.ToObject<decimal>() ?? 0,
                        OurQty = i.SelectToken("OurQty")?.ToObject<decimal>() ?? 0,
                        PONumApprovalStatus = i.SelectToken("PONumApprovalStatus")?.ToString() ?? string.Empty,
                        PONumConfirmed = i.SelectToken("PONumConfirmed")?.ToObject<bool>() ?? false,
                        WareHouseCode = i.SelectToken("WareHouseCode")?.ToString() ?? string.Empty,
                        BinNum = i.SelectToken("BinNum")?.ToString() ?? string.Empty,
                        LotNum = i.SelectToken("LotNum")?.ToString() ?? string.Empty,
                        IUM = i.SelectToken("IUM")?.ToString() ?? string.Empty,
                        PUM = i.SelectToken("PUM")?.ToString() ?? string.Empty,
                        TranType = i.SelectToken("TranType")?.ToString() ?? string.Empty
                    }).ToList();
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetPOInfoWithDs 调用失败: PONum={PONum}", poNum);
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// GetNewRcvDtl 方法（使用指定的 ds）
        /// </summary>
        private async Task<JObject?> GetNewRcvDtlAsync(EpicorAuthService authService, int vendorNum, string packSlip, JObject ds)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/GetNewRcvDtl";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum,
                    purPoint = "",
                    packSlip,
                    ds
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("GetNewRcvDtl 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetNewRcvDtl 调用失败");
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// GetDtlPOLineInfo 方法（使用指定的 ds）
        /// </summary>
        private async Task<JObject?> GetDtlPOLineInfoAsync(EpicorAuthService authService, int vendorNum, string packSlip, int poLine, JObject ds)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/GetDtlPOLineInfo";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum,
                    purPoint = "",
                    packSlip,
                    packLine = 0,
                    poLine = poLine.ToString(CultureInfo.InvariantCulture),
                    ds
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("GetDtlPOLineInfo 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDtlPOLineInfo 调用失败");
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// GetDtlQtyInfo 方法（使用指定的 ds）
        /// </summary>
        private async Task<JObject?> GetDtlQtyInfoAsync(EpicorAuthService authService, int vendorNum, string packSlip, decimal qty, JObject ds)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/GetDtlQtyInfo";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum,
                    purPoint = "",
                    packSlip,
                    packLine = 0,
                    inputOurQty = qty.ToString(CultureInfo.InvariantCulture),
                    inputIUM = "",
                    whichField = "QTY",
                    ds
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("GetDtlQtyInfo 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDtlQtyInfo 调用失败");
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// OnChangeDtlReceived 方法（使用指定的 ds 和 ipReceived）
        /// </summary>
        private async Task<JObject?> OnChangeDtlReceivedAsync(EpicorAuthService authService, int vendorNum, string packSlip, JObject ds)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/OnChangeDtlReceived";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum,
                    purPoint = "",
                    packSlip,
                    packLine = 0,
                    ipReceived = "true",
                    ds
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("OnChangeDtlReceived 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnChangeDtlReceived 调用失败");
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// CheckOnLeaveHead 方法
        /// </summary>
        private async Task<JObject?> CheckOnLeaveHeadAsync(EpicorAuthService authService, int vendorNum, string packSlip)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/CheckOnLeaveHead";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum,
                    purPoint = "",
                    packSlip
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("CheckOnLeaveHead 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CheckOnLeaveHead 调用失败");
                // 不透传 ex.Message（HttpRequestException 等常含内部主机名/URL），
                //     详细异常已由上方 LogError 完整记录，无信息损失
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// CheckDtlLotInfo 方法 — 验证批号信息
        /// </summary>
        private async Task<JObject?> CheckDtlLotInfoAsync(EpicorAuthService authService, int vendorNum, string packSlip, string lotNum, JObject ds)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/CheckDtlLotInfo";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum,
                    purPoint = "",
                    packSlip,
                    packLine = 0,
                    lotNum,
                    ds
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("CheckDtlLotInfo 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CheckDtlLotInfo 调用失败");
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        /// <summary>
        /// GetDtlLotInfo 方法 — 获取和校验批次信息
        /// </summary>
        private async Task<JObject?> GetDtlLotInfoAsync(EpicorAuthService authService, int vendorNum, string packSlip, string lotNum, JObject ds)
        {
            try
            {
                var url = "Erp.BO.ReceiptSvc/GetDtlLotInfo";
                var payload = EpicorJsonHelper.Serialize(new
                {
                    vendorNum,
                    purPoint = "",
                    packSlip,
                    packLine = 0,
                    lotNum,
                    ds
                });
                var responseJson = await authService.PostBoJsonAsync(url, payload);
                var (response, hasError, errorMsg) = EpicorJsonHelper.TryParseResponse(responseJson);
                if (hasError)
                {
                    _logger.LogError("GetDtlLotInfo 错误: {ErrorMsg}", errorMsg);
                    authService.SetApiError(errorMsg);
                    return null;
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDtlLotInfo 调用失败");
                authService.SetApiError(GetText("Receivin", "EpicorApiFailed"));
                return null;
            }
        }

        #endregion
    }
}