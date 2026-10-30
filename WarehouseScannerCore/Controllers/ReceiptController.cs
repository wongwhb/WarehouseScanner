using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>
    /// 工单入库控制器
    /// 
    /// 架构原则：Controller 只做请求调度，不包含业务逻辑
    /// 职责范围：
    ///   1. 身份认证/权限检查
    ///   2. 参数绑定与数据校验（ModelState）
    ///   3. 调用业务服务 ReceiptBusinessService
    ///   4. 统一包装响应（页面/JSON）
    ///   
    /// 业务逻辑所在：WarehouseScannerCore.Services.ReceiptBusinessService
    /// </summary>
    // 页面权限用特性声明
    [RequirePermission(MenuCodes.Inbound)]
    public class ReceiptController : BaseController
    {
        #region 依赖注入
        private readonly ReceiptBusinessService _receiptService;
        private readonly IEpicorAuthServiceFactory _authServiceFactory;
        private readonly ShippingScheduleQueryService _shippingScheduleService;

        public ReceiptController(
            ILogger<ReceiptController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache,
            ILanguageService languageService,
            UserMenuPermissionService permissionService,
            ReceiptBusinessService receiptService,
            ShippingScheduleQueryService shippingScheduleService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _receiptService = receiptService ?? throw new ArgumentNullException(nameof(receiptService));
            _authServiceFactory = epicorServiceFactory ?? throw new ArgumentNullException(nameof(epicorServiceFactory));
            _shippingScheduleService = shippingScheduleService ?? throw new ArgumentNullException(nameof(shippingScheduleService));
        }
        #endregion

        #region 页面入口 - GET
        [HttpGet]
        public IActionResult Index()
        {
            #region 认证 + 权限检查
            if (!IsUserAuthenticated())
            {
                _logger.LogWarning("未登录用户尝试访问工单入库页面，重定向到登录页");
                return RedirectToAction("Login", "Account");
            }

            // 页面权限改由类级 [RequirePermission(MenuCodes.Inbound)] 特性统一声明（见类声明处）
            #endregion

            #region 准备 ViewBag 数据（语言相关）
            var cultureName = Request.Cookies["Language"] ?? CultureInfo.CurrentUICulture.Name;

            ViewBag.GetText = new Func<string, string, string>((section, key) => GetText(section, key, cultureName));
            ViewBag.CurrentCulture = cultureName;
            ViewBag.UserId = HttpContext.Session.GetString(SessionKeys.UserId) ?? string.Empty;
            ViewBag.UserRole = HttpContext.Session.GetString(SessionKeys.UserRole) ?? string.Empty;
            ViewBag.SiteName = EpicorSessionHelper.GetSiteCode(HttpContext.Session);
            ViewBag.LoginTime = HttpContext.Session.GetString(SessionKeys.LoginTime) ?? string.Empty;
            #endregion

            return View();
        }
        #endregion

        #region AJAX - 工单验证
        /// <summary>
        /// 验证工单状态是否允许入库（POST AJAX）
        /// 条件：工单已发布、未关闭/未完工。超收不做数量限制，仅通过 warningMessage 提示，允许入库。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidateJob([FromBody] ValidateReceiptJobRequest request)
        {
            #region 参数校验 + 清洗
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage);
                return Json(new { success = false, message = string.Format(GetLocalText("Receipt", "InvalidParamsWithDetail"), string.Join(" | ", errors)) });
            }

            request.WorkOrderNo = (request.WorkOrderNo ?? "").Trim();
            request.MaterialCode = (request.MaterialCode ?? "").Trim();
            #endregion

            #region 调用业务服务
            var (success, message, data, overReceiptWarning) = await _receiptService.ValidateJobForReceiptAsync(
                request.WorkOrderNo, request.AssemblySeq, request.ScanQty, request.MaterialCode);

            object? responseData = null;
            if (data != null)
            {
                responseData = new
                {
                    jobNum = data.JobNum ?? string.Empty,
                    partNum = data.PartNum ?? string.Empty,
                    jobFirm = data.JobFirm,
                    jobClosed = data.JobClosed,
                    prodQty = data.ProdQty,
                    receivedQty = data.ReceivedQty,
                    availableQty = data.AvailableQty,
                    stockQty = data.StockQty,
                    warehouseCode = data.WarehouseCode ?? string.Empty,
                    binNum = data.BinNum ?? string.Empty,
                    unit = data.Unit ?? string.Empty
                };
            }

            return Json(new { success, message, data = responseData, warningMessage = overReceiptWarning });
            #endregion
        }
        #endregion

    

        #region AJAX - 查询装配序号
        /// <summary>
        /// 根据工单号+物料号查询 AssemblySeq（ReceiptsFromMfgSvc/GetRows）
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GetAssemblySeq([FromBody] GetAssemblySeqRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.JobNum) || string.IsNullOrWhiteSpace(request.PartNum))
            {
                return Json(new { success = false, message = GetLocalText("Receipt", "JobNoAndPartRequired") });
            }

            try
            {
                var authService = CreateEpicorAuthServiceFromSession();
                var receiptService = new EpicorReceiptService(authService, authService.Company ?? "", operatorId: "", plant: authService.Plant);

                // 先验证物料是否存在于系统（PartPlants: 404/查无数据 → 物料不存在）
                var partInfo = await receiptService.GetPartDefaultWarehouseAsync(request.PartNum);
                if (partInfo.HasValue && partInfo.Value.partNotFound)
                {
                    return Json(new { success = false, message = string.Format(GetText("Receipt", "PartNotExists"), request.PartNum), code = "PartNotFound" });
                }
                if (!partInfo.HasValue)
                {
                    // 查不到也可能是接口挂了，先看 HasApiError
                    if (authService.HasApiError)
                        return Json(new { success = false, message = string.Format(GetLocalText("Receipt", "EpicorApiFailed"), authService.LastApiError) });
                    return Json(new { success = false, message = GetText("Receipt", "PartNotFound"), code = "PartNotFound" });
                }

                var seq = await receiptService.GetAssemblySeqFromMfgAsync(request.JobNum, request.PartNum);

                if (seq.HasValue)
                {
                    return Json(new { success = true, assemblySeq = seq.Value });
                }

                // 同上：先看接口有没有报错
                if (authService.HasApiError)
                    return Json(new { success = false, message = string.Format(GetLocalText("Receipt", "EpicorApiFailed"), authService.LastApiError) });

                return Json(new { success = false, message = string.Format(GetText("Receipt", "AssemblySeqNotFound"), request.JobNum, request.PartNum), code = "AssemblySeqNotFound" });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("Receipt", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询装配序号异常: JobNum={JobNum}, PartNum={PartNum}", request.JobNum, request.PartNum);
                return Json(new { success = false, message = GetText("Receipt", "QueryAssemblySeqFailed") });
            }
        }
        #endregion

        #region AJAX - PART+PO 条码 → 查发货计划获取工单号 + 装配序号 + 仓库/库位/单位
        /// <summary>
        /// PART+PO 格式条码扫码入口：
        /// 1. 按 PONum/OrderLine/OrderRelNum 查 ShippingSchedule → 获取工单号
        /// 2. 按工单号+物料号查装配序号（ReceiptsFromMfgSvc/GetRows）
        /// 3. 按物料号+工单号查 BAQ Inve_view → 获取仓库/库位/单位
        /// 返回 { success, jobNum, assemblySeq, warehouseCode, binNum, unit }
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QueryShippingSchedule([FromBody] QueryShippingScheduleRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.PONum) || !request.OrderLine.HasValue || !request.OrderRelNum.HasValue)
            {
                return Json(new { success = false, message = GetLocalText("Receipt", "PoParamsRequired") });
            }

            string partNum = request.PartNum?.Trim() ?? string.Empty;

            try
            {
                // 步骤 1：按 PONum + OrderLine + OrderRelNum 查 ShippingSchedule
                var (schedule, schedApiError) = await _shippingScheduleService.QueryByJobAndPartAsync(
                    request.PONum,
                    request.OrderLine.Value,
                    request.OrderRelNum.Value
                );

                // 接口故障与"查无记录"分流：故障必须透传真实错误，不能被当成"未找到发货计划"
                if (!string.IsNullOrEmpty(schedApiError))
                {
                    return Json(new { success = false, message = string.Format(GetLocalText("Receipt", "EpicorApiFailed"), schedApiError) });
                }

                if (schedule == null)
                {
                    return Json(new { success = false, message = string.Format(GetText("Receipt", "ShippingScheduleNotFound"), request.PONum, request.OrderLine.Value, request.OrderRelNum.Value), code = "ShippingScheduleNotFound" });
                }

                string jobNum = schedule.JobNum ?? string.Empty;

                // 创建一次 authService，供步骤 2 和步骤 3 共用
                var authService = CreateEpicorAuthServiceFromSession();

                // 步骤 2：按工单号+物料号查装配序号（ReceiptsFromMfgSvc/GetRows）
                int? assemblySeq = null;
                if (!string.IsNullOrWhiteSpace(jobNum) && !string.IsNullOrWhiteSpace(partNum))
                {
                    try
                    {
                        var receiptService = new EpicorReceiptService(authService, authService.Company ?? "", operatorId: "", plant: authService.Plant);
                        var seq = await receiptService.GetAssemblySeqFromMfgAsync(jobNum, partNum);
                        if (seq.HasValue) assemblySeq = seq.Value;
                    }
                    catch (Exception asmEx)
                    {
                        _logger.LogWarning(asmEx, "装配序号查询失败（不阻断流程）: JobNum={JobNum}, PartNum={PartNum}", jobNum, partNum);
                    }
                }

                // 步骤 3：按物料号+工单号查 BAQ Inve_view → 仓库/库位/单位
                string unit = string.Empty;
                string warehouseCode = string.Empty;
                string binNum = string.Empty;

                if (!string.IsNullOrWhiteSpace(partNum))
                {
                    try
                    {
                        var inveResult = await authService.QueryInveViewByBAQAsync(partNum, jobNum);
                        if (inveResult != null)
                        {
                            unit = inveResult.Unit ?? string.Empty;
                            warehouseCode = inveResult.WarehouseCode ?? string.Empty;
                            binNum = inveResult.BinNum ?? string.Empty;
                        }
                    }
                    catch (Exception baqEx)
                    {
                        _logger.LogWarning(baqEx, "BAQ Inve_view 查询失败（不阻断流程）: PartNum={PartNum}, LotNum={LotNum}", partNum, jobNum);
                    }
                }

                return Json(new
                {
                    success = true,
                    jobNum = jobNum,
                    assemblySeq = assemblySeq,
                    unit = unit,
                    warehouseCode = warehouseCode,
                    binNum = binNum
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("Receipt", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueryShippingSchedule 异常: PONum={PONum}, OrderLine={OrderLine}, OrderRelNum={OrderRelNum}, PartNum={PartNum}",
                    request.PONum, request.OrderLine, request.OrderRelNum, partNum);
                return Json(new { success = false, message = GetText("Receipt", "QueryShippingScheduleFailed") });
            }
        }
        #endregion

        #region AJAX - 执行入库
        /// <summary>
        /// 执行工单入库（POST AJAX）
        /// X-Request-ID Header：用于幂等性保护（可选）
        /// 业务允许超收，超收警告通过 warningMessage 返回，不阻断入库。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReceiveMfgPart(
            [FromForm] ReceiveMfgPartRequest request,
            [FromHeader(Name = "X-Request-ID")] string? requestId = null)
        {
            #region 参数校验
            if (!ModelState.IsValid)
                return Json(new { success = false, message = GetLocalText("Receipt", "InvalidParams") });

            request.WorkOrderNo = (request.WorkOrderNo ?? "").Trim();
            request.MaterialCode = (request.MaterialCode ?? "").Trim();

            if (string.IsNullOrWhiteSpace(request.WorkOrderNo))
                return Json(new { success = false, message = GetLocalText("Receipt", "WorkOrderRequired") });
            if (request.Quantity <= 0)
                return Json(new { success = false, message = GetLocalText("Receipt", "QuantityRequired") });
            // 仓库/库位/批次号必填（存在性校验下沉到 Service 层 ValidateJobForReceiptAsync，避免重复 API 调用）
            if (string.IsNullOrWhiteSpace(request.WarehouseCode))
                return Json(new { success = false, message = GetLocalText("Receipt", "WarehouseCodeRequired") });
            if (string.IsNullOrWhiteSpace(request.Location))
                return Json(new { success = false, message = GetLocalText("Receipt", "LocationRequired") });
            if (string.IsNullOrWhiteSpace(request.BatchNo))
                return Json(new { success = false, message = GetLocalText("Receipt", "BatchNoRequired") });
            #endregion

            try
            {
                #region 调用业务服务
                string operatorId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";

                var (success, message, data, warningMessage) = await _receiptService.ReceiveMfgPartAsync(
                    request, operatorId, requestId);

                object? responseData = null;
                if (data != null)
                {
                    responseData = new
                    {
                        workOrderNo = data.WorkOrderNo ?? string.Empty,
                        assemblySeq = data.AssemblySeq,
                        materialCode = data.MaterialCode ?? string.Empty,
                        quantity = data.Quantity,
                        batchNo = data.BatchNo,
                        location = data.Location,
                        @operator = data.Operator ?? string.Empty,
                        receiptTime = data.ReceiptTime ?? string.Empty,
                        tranNum = data.TranNum,
                        warningMessage = data.WarningMessage ?? string.Empty
                    };
                }

                return Json(new { success, message, data = responseData, warningMessage });
                #endregion
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("Receipt", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工单入库异常: WorkOrderNo={WorkOrderNo}", request?.WorkOrderNo);
                return Json(new { success = false, message = GetText("Receipt", "SystemProcessingError") });
            }
        }
        #endregion
    }
}