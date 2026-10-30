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
using WarehouseScannerCore.Utilities;
namespace WarehouseScanner.Services
{
    /// <summary>业务方法 - 工单 / 销售订单 / 其他</summary>
    public partial class EpicorAuthService
    {
        #region 业务方法 - 工单相关
        public async Task<List<JobMtlSearchResult>> QueryJobMtlIssued(string jobNum, int assemblySeq, string partNum, CancellationToken cancellationToken = default)
        {
            ClearApiError(); // 重置错误状态
            try
            {
                var filterList = new List<string>
   {
       ODataFilterBuilder.Eq("JobNum", jobNum),
       ODataFilterBuilder.Eq("AssemblySeq", assemblySeq)
   };
                if (!string.IsNullOrEmpty(partNum))
                    filterList.Add(ODataFilterBuilder.Eq("PartNum", partNum));

                string filter = ODataFilterBuilder.And(filterList.ToArray());
                string url = ODataFilterBuilder.BuildUrl("Erp.BO.JobMtlSearchSvc/JobMtlSearches", filter);
                string body = await GetODataJsonAsync(url, cancellationToken);

                // 【步骤1：检查 API 是否返回错误 JSON】
                if (CheckEpicorApiError(body, $"QueryJobMtlIssued(JobNum={jobNum}, AssemblySeq={assemblySeq})", out _))
                    return new List<JobMtlSearchResult>();

                // 【步骤2：无错误时才执行业务逻辑解析】
                var jObj = JObject.Parse(body);
                var arr = jObj["value"] as JArray;
                if (arr == null) return new List<JobMtlSearchResult>();

                return arr.Select(item => new JobMtlSearchResult
                {
                    PartNum = item.Value<string>("PartNum") ?? string.Empty,
                    RequiredQty = item.Value<decimal?>("RequiredQty") ?? 0m,
                    QtyPreviouslyIssued = item.Value<decimal?>("IssuedQty") ?? 0,
                    DspIUM = item.Value<string>("IUM") ?? string.Empty,
                    BaseUOM = item.Value<string>("BaseUOM") ?? string.Empty,
                    MtlSeq = item.Value<int?>("MtlSeq") ?? 0,
                    RelatedOperation = item.Value<string>("RelatedOperation") ?? string.Empty,
                    IssuedComplete = item.Value<bool?>("IssuedComplete") ?? false
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询工单物料异常 JobNum:{JobNum}", jobNum);
                SetApiError("工单物料查询失败，请稍后重试");
                return new List<JobMtlSearchResult>();
            }
        }

        public async Task<JobStatusValidateResult> ValidateJobStatusAsync(string jobNum, CancellationToken cancellationToken = default)
        {
            ClearApiError(); // 重置错误状态
            var result = new JobStatusValidateResult();
            try
            {
                string filter = ODataFilterBuilder.Eq("JobNum", jobNum);
                string url = ODataFilterBuilder.BuildUrl("Erp.BO.JobEntrySvc/JobEntries", filter);
                string body = await GetODataJsonAsync(url, cancellationToken);

                // 【步骤1：检查 API 是否返回错误 JSON】
                if (CheckEpicorApiError(body, $"ValidateJobStatusAsync(JobNum={jobNum})", out _))
                {
                    result.IsValid = false;
                    return result;
                }

                // 【步骤2：无错误时才执行业务逻辑解析】
                var jObj = JObject.Parse(body);
                var arr = jObj["value"] as JArray;
                if (arr == null || !arr.Any())
                {
                    result.IsValid = false;
                    return result;
                }

                var job = arr.First();
                result.JobNum = job.Value<string>("JobNum") ?? string.Empty;
                result.PartNum = job.Value<string>("PartNum") ?? string.Empty;
                result.JobReleased = job.Value<bool?>("JobReleased") ?? false;
                result.JobComplete = job.Value<bool?>("JobComplete") ?? false;
                result.JobClosed = job.Value<bool?>("JobClosed") ?? false;

                result.IsValid = result.JobReleased && !result.JobComplete && !result.JobClosed;
                return result;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                _logger.LogError(ex, "工单状态校验异常 JobNum:{JobNum}", jobNum);
                SetApiError("工单状态校验失败，请稍后重试");
                return result;
            }
        }
        #endregion

        #region 业务方法 - 销售订单相关
        public async Task<List<OrderRelInfo>> QueryOrderRelsBySalesOrder(string orderNum, int orderLine, CancellationToken cancellationToken = default)
        {
            ClearApiError(); // 重置错误状态
            try
            {
                // 安全：orderNum 进入 OData key path 前必须是纯数字，防止 OData 注入
                if (!long.TryParse(orderNum, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    _logger.LogWarning("QueryOrderRelsBySalesOrder: orderNum 非纯数字，拒绝查询。OrderNum={OrderNum}", orderNum);
                    return new List<OrderRelInfo>();
                }

                string url = $"Erp.BO.SalesOrderSvc/OrderDtls('{_company}',{orderNum},{orderLine})?$expand=OrderRels";
                string body = await GetODataJsonAsync(url, cancellationToken);

                // 【步骤1：检查 API 是否返回错误 JSON】
                if (CheckEpicorApiError(body, $"QueryOrderRelsBySalesOrder(OrderNum={orderNum}, Line={orderLine})", out _))
                    return new List<OrderRelInfo>();

                // 【步骤2：无错误时才执行业务逻辑解析】
                var jObj = JObject.Parse(body);
                var arr = jObj["OrderRels"] as JArray;

                if (arr == null) return new List<OrderRelInfo>();
                return arr.Select(item => new OrderRelInfo
                {
                    OrderQty = item.Value<decimal?>("SellingReqQty") ?? 0m,
                    TotalJobStockShipped = item.Value<decimal?>("TotalJobStockShipped") ?? 0,
                    //包装数量（订单分配数量）
                    SellingRemainQty = item.Value<decimal?>("AllocatedQuantity") ?? 0,
                    ShipLocked_c = item.Value<string>("ShipLocked_c") ?? string.Empty,
                    ShipViaCode = item.Value<string>("ShipViaCode") ?? string.Empty,
                    PartNum = item.Value<string>("PartNum") ?? string.Empty,
                    IUM = item.Value<string>("IUM") ?? string.Empty,
                    FirmRelease = item.Value<string>("FirmRelease") ?? string.Empty,
                    Status = item.Value<string>("ReleaseStatus") ?? string.Empty,
                    OrderRelNum = item.Value<int?>("OrderRelNum") ?? 0
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询订单交付异常 OrderNum:{OrderNum} Line:{Line}", orderNum, orderLine);
                SetApiError("订单交付信息查询失败，请稍后重试");
                return new List<OrderRelInfo>();
            }
        }

        public async Task<List<CustomerOrderShipmentResult>> GetCustomerOrderShipments(string custID = "", string custName = "", string orderNum = "", string poNum = "", CancellationToken cancellationToken = default)
        {
            ClearApiError(); // 重置错误状态
            try
            {
                // BAQ where: Customer.CustID like @CustNum, Customer.Name like @CustName → string 加 % 模糊
                string paramCustID = string.IsNullOrWhiteSpace(custID) ? "" : "%" + custID.Trim() + "%";
                string paramCustName = string.IsNullOrWhiteSpace(custName) ? "" : "%" + custName.Trim() + "%";

                // BAQ join: OrderHed.OrderNum = @OrderNum → int 精确匹配，不加 %
                int? paramOrderNum = null;
                if (int.TryParse(orderNum?.Trim(), out var onum)) paramOrderNum = onum;

                // BAQ join: OrderHed.PONum = @PONum → string 精确匹配，不加 %
                string paramPONum = string.IsNullOrWhiteSpace(poNum) ? "" : poNum.Trim();

                // 传递全部参数（含空值 IsEmpty=true），让 BAQ 跳过空值对应的 JOIN/WHERE 条件
                var paramList = new List<object>
                {
                    new { ParameterID = "CustNum", ParameterValue = paramCustID, ValueType = "string", IsEmpty = string.IsNullOrEmpty(paramCustID) },
                    new { ParameterID = "CustName", ParameterValue = paramCustName, ValueType = "string", IsEmpty = string.IsNullOrEmpty(paramCustName) },
                    new { ParameterID = "OrderNum", ParameterValue = paramOrderNum ?? 0, ValueType = "int", IsEmpty = !paramOrderNum.HasValue },
                    new { ParameterID = "PONum", ParameterValue = paramPONum, ValueType = "string", IsEmpty = string.IsNullOrEmpty(paramPONum) }
                };
                AddPlantParameter(paramList);

                string json = JsonConvert.SerializeObject(new
                {
                    queryID = "DO_CustomerOrderShipmentList",
                    executionParams = new
                    {
                        ExecutionParameter = paramList
                    }
                }, _jsonSettings);

                string body = await PostBoJsonAsync("Ice.BO.DynamicQuerySvc/ExecuteByID", json, cancellationToken, requireLicense: false);

                // 【步骤1：检查 API 是否返回错误 JSON】
                if (CheckEpicorApiError(body, $"GetCustomerOrderShipments(CustID={custID},CustName={custName},OrderNum={orderNum},PONum={poNum})", out _))
                    return new List<CustomerOrderShipmentResult>();

                // 【步骤2：无错误时才执行业务逻辑解析】
                var jObj = JObject.Parse(body);
                var arr = jObj["returnObj"]?["Results"] as JArray;
                if (arr == null) return new List<CustomerOrderShipmentResult>();

                return arr.Select(item => new CustomerOrderShipmentResult
                {
                    CustNum = item["Customer_CustID"]?.Value<string>() ?? string.Empty,
                    CustName = item["Customer_Name"]?.Value<string>() ?? string.Empty,
                    CustomerReqDate = item["OrderRel_ReqDate"]?.Value<string>() ?? string.Empty,
                    PO = item["OrderHed_PONum"]?.Value<string>() ?? string.Empty,
                    Order = item["OrderDtl_OrderNum"]?.Value<string>() ?? string.Empty,
                    Line = item["OrderDtl_OrderLine"]?.Value<string>() ?? string.Empty,
                    Rel = item["OrderRel_OrderRelNum"]?.Value<string>() ?? string.Empty,
                    Part = item["OrderDtl_PartNum"]?.Value<string>() ?? string.Empty,
                    PartDescription = item["OrderDtl_LineDesc"]?.Value<string>() ?? string.Empty,
                    OurReqQty = item["OrderRel_OurReqQty"]?.Value<decimal?>() ?? 0,
                    OnHandQty = item["Calculated_Calculated_QtyReq"]?.Value<decimal?>() ?? 0,
                    Comments = item["OrderRel_Comments_c"]?.Value<string>() ?? string.Empty
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询客户发货单异常 CustID:{CustID}, CustName:{CustName}, OrderNum:{OrderNum}, PONum:{PONum}", custID, custName, orderNum, poNum);
                SetApiError("客户发货单查询失败，请稍后重试");
                return new List<CustomerOrderShipmentResult>();
            }
        }

        public async Task<JObject?> GetCustShipByPackNumAsync(int packNum, CancellationToken cancellationToken = default)
        {
            ClearApiError(); // 重置错误状态
            try
            {
                string url = $"Erp.BO.CustShipSvc/GetRows?$filter=PackNum eq {packNum} and PackLine eq 0";
                string body = await GetODataJsonAsync(url, cancellationToken);

                // 【步骤1：检查 API 是否返回错误 JSON】
                if (CheckEpicorApiError(body, $"GetCustShipByPackNumAsync(PackNum={packNum})", out _))
                    return null;

                // 【步骤2：无错误时才执行业务逻辑解析】
                var jObj = JObject.Parse(body);
                return new JObject
                {
                    ["ds"] = new JObject
                    {
                        ["ShipHead"] = new JArray { jObj }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据装箱号查询发货单异常 PackNum:{Pack}", packNum);
                SetApiError("发货单查询失败，请稍后重试");
                return null;
            }
        }
        #endregion

        #region 业务方法 - 其他
        public async Task<WhseBinInfo?> GetBinInfo(string warehouseCode, string binNum, CancellationToken cancellationToken = default)
        {
            ClearApiError(); // 重置错误状态
            if (string.IsNullOrEmpty(warehouseCode) || string.IsNullOrEmpty(binNum))
                return null;
            try
            {
                string url = $"Erp.BO.WhseBinSvc/GetByID?warehouseCode={Uri.EscapeDataString(warehouseCode)}&binNum={Uri.EscapeDataString(binNum)}";
                string body = await GetODataJsonAsync(url, cancellationToken);

                // 【步骤1：检查 API 是否返回错误 JSON】
                if (CheckEpicorApiError(body, $"GetBinInfo(Whse={warehouseCode}, Bin={binNum})", out _))
                    return null;

                // 【步骤2：无错误时才执行业务逻辑解析】
                var jObj = JObject.Parse(body);
                var whseBinArray = jObj["returnObj"]?["WhseBin"] as JArray;
                var binItem = whseBinArray?.FirstOrDefault() as JObject;

                if (binItem == null)
                    return null;

                var binNumValue = binItem["BinNum"]?.ToString();
                if (string.IsNullOrEmpty(binNumValue))
                    return null;

                return new WhseBinInfo
                {
                    WarehouseCode = binItem["WarehouseCode"]?.ToString() ?? "",
                    BinNum = binNumValue,
                    Description = binItem["Description"]?.ToString() ?? "",
                    InActive = binItem["InActive"]?.ToObject<bool?>()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询库位信息异常 Whse:{Whse} Bin:{Bin}", warehouseCode, binNum);
                SetApiError("库位信息查询失败，请稍后重试");
                return null;
            }
        }

        public async Task<LotInfo?> GetLotInfo(string company, string partNum, string lotNumber, CancellationToken cancellationToken = default)
        {
            ClearApiError(); // 重置错误状态
            try
            {
                // 安全：OData key 路径中单引号字面量必须先翻倍再 URL 编码，防止注入
                string key = $"('{Uri.EscapeDataString(ODataFilterBuilder.EscapeString(company))}','{Uri.EscapeDataString(ODataFilterBuilder.EscapeString(partNum))}','{Uri.EscapeDataString(ODataFilterBuilder.EscapeString(lotNumber))}')";
                string url = $"Erp.BO.LotSelectUpdateSvc/LotSelectUpdates{key}";
                string body = await GetODataJsonAsync(url, cancellationToken);

                // 【步骤1：检查 API 是否返回错误 JSON】
                if (CheckEpicorApiError(body, $"GetLotInfo(PartNum={partNum}, Lot={lotNumber})", out _))
                    return null;

                // 【步骤2：无错误时才执行业务逻辑解析】
                return JsonConvert.DeserializeObject<LotInfo>(body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询批次信息异常 Part:{Part} Lot:{Lot}", partNum, lotNumber);
                SetApiError("批次信息查询失败，请稍后重试");
                return null;
            }
        }

        #endregion 业务方法 - 其他
    }
}