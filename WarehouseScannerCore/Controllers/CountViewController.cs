using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>
    /// 周期盘点维护查询控制器（与盘点标签 CountTag 分离，独立负责查询界面与数据接口）
    /// </summary>
    // 页面权限用特性声明
    [RequirePermission(MenuCodes.Inventory)]
    public class CountViewController : BaseController
    {
        private readonly IEpicorQueryGateway _queryGateway;

        public CountViewController(
            ILanguageService languageService,
            ILogger<CountViewController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IMemoryCache memoryCache,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IEpicorQueryGateway queryGateway,
            UserMenuPermissionService permissionService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _queryGateway = queryGateway ?? throw new ArgumentNullException(nameof(queryGateway));
        }

        #region 业务 - 查询页面
        [HttpGet]
        public IActionResult Index()
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问周期盘点查询页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                SetupViewBagLocalization();
                return View();
            }
            catch (UnauthorizedAccessException)
            {
                return RedirectToAction("Login", "Account");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问周期盘点维护查询页面时发生错误");
                return RedirectToAction("Login", "Account");
            }
        }
        #endregion

        #region 业务 - 查询接口
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> GetCycleCount([FromBody] CycleCountQueryRequest request)
        {
            try
            {
                string traceId = Guid.NewGuid().ToString("N");

                if (string.IsNullOrWhiteSpace(request.WarehouseCode))
                    return Json(new { success = false, message = GetLocalText("CycleCount", "WarehouseRequired") });

                if (request.CycleDate == default)
                    return Json(new { success = false, message = GetLocalText("CycleCount", "CycleDateRequired") });

                EpicorAuthService authService;
                try
                {
                    authService = CreateEpicorAuthServiceFromSession();
                }
                catch (UnauthorizedAccessException)
                {
                    return Json(new { success = false, message = GetLocalText("CountTag", "SessionExpired") });
                }

                // 构建 whereClauseCCHdr
                // 安全：仓库代码白名单校验 + OData 单引号转义，防止 OData 注入
                if (!ODataFilterBuilder.IsValidCode(request.WarehouseCode))
                {
                    _logger.LogWarning("[{TraceId}] 周期盘点查询被拒绝：仓库代码格式非法 {Whse}",
                        traceId, request.WarehouseCode);
                    return Json(new { success = false, message = GetLocalText("CycleCount", "WarehouseCodeInvalid") });
                }

                // Epicor BO URL 构造与调用统一下沉到 EpicorBoQueryHelper，
                //   Controller 不直接拼接 Erp.BO.* 地址。
                //   注意：仍使用本方法的 authService 实例，其 HasApiError / LastApiError
                //   依旧是下方判错的依据，错误传递链路未变。
                var response = await EpicorBoQueryHelper.GetCountCycleRowsAsync(
                    authService, request.WarehouseCode, request.CycleDate);

                if (string.IsNullOrWhiteSpace(response))
                {
                    var msg = authService.HasApiError
                        ? $"{GetLocalText("CycleCount", "QueryFailed")}：{authService.LastApiError}"
                        : GetLocalText("CycleCount", "NoData");
                    _logger.LogError("[{TraceId}] 查询周期盘点失败：Epicor 返回空响应，仓库 {Whse}，错误 {Error}",
                        traceId, request.WarehouseCode, authService.LastApiError ?? msg);
                    return Json(new { success = false, message = msg });
                }

                var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(response);
                var ccHdrArray = jsonObj.SelectToken("returnObj.CCHdr") as Newtonsoft.Json.Linq.JArray;
                var ccDtlArray = jsonObj.SelectToken("returnObj.CCDtl") as Newtonsoft.Json.Linq.JArray;

                if (ccHdrArray == null || ccHdrArray.Count == 0)
                    return Json(new { success = true, message = GetLocalText("CycleCount", "NoData"), headers = new object[0], details = new object[0] });

                // 提取表头信息（日期直接格式化为 yyyy-MM-dd 字符串，避免前端 UTC 转换偏差）
                var headers = ccHdrArray.Select(h => new
                {
                    cycleStatusDesc = h["CycleStatusDesc"]?.ToString() ?? "",
                    warehouseCode = h["WarehouseCode"]?.ToString() ?? "",
                    warehouseDesc = h["CCHdrWarehseDescription"]?.ToString() ?? "",
                    cycleDate = ToDateString(h["CycleDate"]),
                    tagGenDate = ToDateString(h["TagGenDate"]),
                    totalParts = h["TotalParts"]?.ToObject<int>() ?? 0,
                    cycleSeq = h["CycleSeq"]?.ToObject<int>() ?? 0,
                    ccYear = h["CCYear"]?.ToObject<int>() ?? 0
                }).ToList();

                // 提取明细信息
                var details = (ccDtlArray ?? new Newtonsoft.Json.Linq.JArray())
                    .Select(d => new
                    {
                        partNum = d["PartNum"]?.ToString() ?? "",
                        partDescription = d["PartNumPartDescription"]?.ToString() ?? "",
                        qtyAdjustmentStatus = d["QtyAdjustmentStatus"]?.ToString() ?? "",
                        abcCode = d["ABCCode"]?.ToString() ?? "",
                        baseUOM = d["BaseUOM"]?.ToString() ?? "",
                        totFrozenQOH = d["TotFrozenQOH"]?.ToObject<decimal>() ?? 0,
                        totCountQOH = d["TotCountQOH"]?.ToObject<decimal>() ?? 0
                    }).ToList();

                _logger.LogInformation("查询周期盘点成功: 仓库 {Whse}, 明细 {Count} 条", request.WarehouseCode, details.Count);

                return Json(new
                {
                    success = true,
                    message = GetLocalText("CycleCount", "QuerySuccess"),
                    headers,
                    details
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询周期盘点时发生异常");
                return Json(new { success = false, message = GetLocalText("CycleCount", "QueryFailed") });
            }
        }
        #endregion

        /// <summary>
        /// 将 JToken 日期安全转换为 yyyy-MM-dd 字符串。
        /// Epicor 返回的日期字段可能为 JSON null（值为 null 的 JValue，而非 C# null），
        /// 此时 ?. 不生效，直接 ToObject&lt;DateTime&gt; 会抛 "Can not convert Null to DateTime"。
        /// </summary>
        private static string ToDateString(Newtonsoft.Json.Linq.JToken? token)
        {
            if (token == null || token.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                return "";
            return token.ToObject<DateTime>().ToString("yyyy-MM-dd");
        }

        #region 业务 - 明细库存查询
        /// <summary>
        /// 根据物料代码 + 仓库 查询 PartBin 库存明细
        /// 返回：仓库代码(whseCode)、Bin位(binNum)、批号(lotNumber)、库存数量(qtyOnHand)、单位(ium) 列表
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> QueryInventoryDetail([FromBody] CycleCountInventoryDetailRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.PartNum))
                    return Json(new { success = false, message = GetLocalText("CycleCount", "PartNum") + GetLocalText("Common", "RequiredField") });
                if (string.IsNullOrWhiteSpace(request.WarehouseCode))
                    return Json(new { success = false, message = GetLocalText("CycleCount", "WarehouseRequired") });

                // 按用户要求：使用 Epicor BAQ Inve_view 查询明细库存，批号取自 [PartBin].[LotNum]（PartBin_LotNum）
                // 不走标准 PartBinSearchSvc（避免影响其他模块批号字段）
                var inv = await _queryGateway.GetInventoryDetailByInveViewBAQAsync(
                    request.PartNum.Trim(), request.WarehouseCode.Trim());

                if (inv == null || inv.Count == 0)
                {
                    return Json(new
                    {
                        success = true,
                        items = new object[0]
                    });
                }

                var items = inv
                    .Select(i => new
                    {
                        whseCode = i.WhseCode ?? string.Empty,
                        binNum = i.BinNum ?? string.Empty,
                        lotNumber = i.LotNumber ?? string.Empty,
                        qtyOnHand = i.QtyOnHand,
                        ium = i.IUM ?? string.Empty
                    })
                    .ToList();

                return Json(new { success = true, items });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetLocalText("CountTag", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询周期盘点明细库存异常: Part={PartNum}, Whse={Whse}", request?.PartNum, request?.WarehouseCode);
                return Json(new { success = false, message = GetLocalText("CycleCount", "QueryFailed") });
            }
        }
        #endregion

    }

    /// <summary>
    /// 周期盘点查询请求模型（与 CountTag 模型分离，独立存放于查询控制器文件）
    /// </summary>
    public class CycleCountQueryRequest
    {
        public DateTime CycleDate { get; set; }
        public string WarehouseCode { get; set; } = null!;
    }

    /// <summary>
    /// 明细库存查询请求模型（按 物料 + 仓库）
    /// </summary>
    public class CycleCountInventoryDetailRequest
    {
        public string PartNum { get; set; } = null!;
        public string WarehouseCode { get; set; } = null!;
    }
}
