﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;
using WarehouseScannerCore.ViewModels;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>
    /// 发料控制器（精简版）
    /// 
    /// 架构原则：Controller 只做请求调度，不包含业务逻辑
    /// 职责范围：
    ///   1. 身份认证/权限检查
    ///   2. 参数绑定与数据校验（ModelState）
    ///   3. 调用业务服务 IssueBusinessService
    ///   4. 统一包装响应（页面/JSON）
    ///   
    /// 业务逻辑所在：WarehouseScannerCore.Services.IssueBusinessService
    /// </summary>
    // 页面权限用特性声明
    [RequirePermission(MenuCodes.Issue)]
    public class IssueController : BaseController
    {
        #region 依赖注入
        private readonly IssueBusinessService _issueService;
        private readonly IEpicorAuthServiceFactory _authServiceFactory;

        public IssueController(
            ILogger<IssueController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache,
            ILanguageService languageService,
            UserMenuPermissionService permissionService,
            IssueBusinessService issueService)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache, languageService, permissionService)
        {
            _issueService = issueService ?? throw new ArgumentNullException(nameof(issueService));
            _authServiceFactory = epicorServiceFactory ?? throw new ArgumentNullException(nameof(epicorServiceFactory));
        }
        #endregion

        #region 页面入口 - GET
        /// <summary>
        /// 发料首页（GET）
        /// 使用强类型 IssueIndexViewModel 替代 ViewBag
        /// </summary>
        [HttpGet]
        public IActionResult Index()
        {
            #region 认证 + 权限检查
            if (!IsUserAuthenticated())
            {
                _logger.LogWarning("未登录用户尝试访问发料页面，重定向到登录页");
                return RedirectToAction("Login", "Account");
            }

            // 页面权限由类级 [RequirePermission(MenuCodes.Issue)] 特性统一声明（见类声明处），
            // 覆盖全部 Action，避免重复代码。
            #endregion

            #region 准备 ViewModel 数据 + 兼容 ViewBag（View 仍以 ViewBag.GetText 等方式取值）
            var cultureName = Request.Cookies["Language"] ?? CultureInfo.CurrentUICulture.Name;

            // 统一设置 ViewBag 本地化/用户信息（替代重复的 lambda + ViewBag 赋值）
            SetupViewBagLocalization();

            var model = new IssueIndexViewModel
            {
                CurrentCulture = cultureName,
                UserId = ViewBag.UserId as string ?? string.Empty,
                UserRole = ViewBag.UserRole as string ?? string.Empty,
                SiteName = ViewBag.SiteName as string ?? string.Empty,
                LoginTime = ViewBag.LoginTime as string ?? string.Empty,

                // 预加载发料页面常用的本地化文本
                Text = new Dictionary<string, string>
                {
                    ["MaterialIssue.Title"] = GetText("MaterialIssue", "Title", cultureName),
                    ["MaterialIssue.JobStatus"] = GetText("MaterialIssue", "JobStatus", cultureName),
                    ["MaterialIssue.JobNotFound"] = GetText("MaterialIssue", "JobNotFound", cultureName),
                    ["MaterialIssue.MaterialNotFound"] = GetText("MaterialIssue", "MaterialNotFound", cultureName),
                    ["MaterialIssue.UnitMismatch"] = GetText("MaterialIssue", "UnitMismatch", cultureName),
                    ["MaterialIssue.MaterialFullyIssued"] = GetText("MaterialIssue", "MaterialFullyIssued", cultureName),
                    ["MaterialIssue.NoAvailableInventory"] = GetText("MaterialIssue", "NoAvailableInventory", cultureName),
                    ["MaterialIssue.InsufficientInventory"] = GetText("MaterialIssue", "InsufficientInventory", cultureName),
                    ["MaterialIssue.QuantityExceedsInventory"] = GetText("MaterialIssue", "QuantityExceedsInventory", cultureName),
                    ["MaterialIssue.SubmitSuccess"] = GetText("MaterialIssue", "SubmitSuccess", cultureName),
                    ["MaterialIssue.JobValidationPassed"] = GetText("MaterialIssue", "JobValidationPassed", cultureName),
                    ["MaterialIssue.InventoryValidationPassed"] = GetText("MaterialIssue", "InventoryValidationPassed", cultureName),
                    ["MaterialIssue.WorkOrderRequired"] = GetText("MaterialIssue", "WorkOrderRequired", cultureName),
                    ["MaterialIssue.QuantityRequired"] = GetText("MaterialIssue", "QuantityRequired", cultureName),
                    ["MaterialIssue.InvalidQuantity"] = GetText("MaterialIssue", "InvalidQuantity", cultureName)
                }
            };
            #endregion

            return View(model);
        }
        #endregion

        #region AJAX - 工单验证
        /// <summary>
        /// 验证工单状态（POST AJAX）
        /// [ValidateAntiForgeryToken]：启用 CSRF 防护
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidateJob([FromBody] ValidateJobRequest request)
        {
            #region 参数校验
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage);
                return Json(new { success = false, message = string.Format(GetText("MaterialIssue", "InvalidParamsWithDetail"), string.Join(" | ", errors)) });
            }
            #endregion

            try
            {
                #region 调用业务服务
                var (success, message) = await _issueService.ValidateJobAsync(
                    request.WorkOrderNo, request.AssemblySeq);

                return Json(new { success, message });
                #endregion
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("MaterialIssue", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证工单出错");
                return Json(new { success = false, message = GetText("MaterialIssue", "JobValidationFailed") });
            }
        }
        #endregion

        #region AJAX - 库存验证
        /// <summary>
        /// 验证物料 + 库存（POST AJAX）
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidateInventory([FromBody] ValidateInventoryRequest request)
        {
            if (!ModelState.IsValid)
                return Json(new { success = false, message = GetText("MaterialIssue", "InvalidParams") });

            try
            {
                var (success, message, unissuedQty, availableQty, suggestedQty, requiredQty, issuedQty) = await _issueService.ValidateInventoryAsync(request);
                return Json(new { success, message, unissuedQty, availableQty, suggestedQty, requiredQty, issuedQty });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("MaterialIssue", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证库存出错");
                return Json(new { success = false, message = GetText("MaterialIssue", "InventoryValidationFailed") });
            }
        }
        #endregion

        #region AJAX - 执行发料
        /// <summary>
        /// 执行发料（POST AJAX）
        /// X-Request-ID Header：用于幂等性保护（可选）
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> IssueMaterial(
            [FromForm] IssueMaterialRequest request,
            [FromHeader(Name = "X-Request-ID")] string? requestId = null)
        {
            #region 参数校验
            if (!ModelState.IsValid)
                return Json(new { success = false, message = GetText("MaterialIssue", "InvalidParams") });

            if (string.IsNullOrWhiteSpace(request.WorkOrderNo))
                return Json(new { success = false, message = GetText("MaterialIssue", "WorkOrderRequired") });
            if (request.AssemblySeq < 0)
                return Json(new { success = false, message = GetText("MaterialIssue", "AssemblySeqInvalid") });
            if (string.IsNullOrWhiteSpace(request.MaterialCode))
                return Json(new { success = false, message = GetText("MaterialIssue", "MaterialCodeRequiredMsg") });
            // 仓库必须显式指定：仓库为空会使库存校验退化为全厂汇总，超发拦截失效
            if (string.IsNullOrWhiteSpace(request.FromWarehouse))
                return Json(new { success = false, message = GetText("MaterialIssue", "FromWarehouseRequired") });
            if (request.Quantity <= 0)
                return Json(new { success = false, message = GetText("MaterialIssue", "QuantityMustBePositive") });
            #endregion

            try
            {
                #region 调用业务服务
                string operatorId = HttpContext.Session.GetString(SessionKeys.UserId) ?? "Unknown";

                var (success, message, data) = await _issueService.IssueMaterialAsync(
                    request, operatorId, requestId);

                return Json(new { success, message, data });
                #endregion
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("MaterialIssue", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行发料出错");
                return Json(new { success = false, message = GetText("MaterialIssue", "IssueMaterialFailed") });
            }
        }
        #endregion

        #region AJAX - 查询工单 / 物料信息
        /// <summary>
        /// 获取工单信息（GET AJAX）
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetWorkOrderInfo(string workOrderNo)
        {
            if (string.IsNullOrWhiteSpace(workOrderNo))
                return Json(new { success = false, message = GetText("MaterialIssue", "WorkOrderRequired") });

            try
            {
                var (success, message, data) = await _issueService.GetJobInfoAsync(workOrderNo);

                if (data == null)
                    return Json(new { success = false, message });

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        jobNum = data.JobNum,
                        partNum = data.PartNum,
                        jobReleased = data.JobReleased,
                        jobComplete = data.JobComplete,
                        jobClosed = data.JobClosed
                    }
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("MaterialIssue", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询工单信息出错");
                return Json(new { success = false, message = GetText("MaterialIssue", "QueryJobInfoFailed") });
            }
        }

        /// <summary>
        /// 获取物料库存信息（GET AJAX）
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMaterialInfo(string materialCode)
        {
            if (string.IsNullOrWhiteSpace(materialCode))
                return Json(new { success = false, message = GetText("MaterialIssue", "MaterialCodeRequiredMsg") });

            try
            {
                var (success, message, dataList) = await _issueService.GetMaterialInfoAsync(materialCode);

                // 优先透传业务服务的错误信息（含 Epicor API 错误），避免掩盖真实失败原因
                if (!success)
                    return Json(new { success = false, message = string.IsNullOrEmpty(message) ? GetText("MaterialIssue", "MaterialInventoryNotFound") : message });

                if (dataList == null || dataList.Count == 0)
                    return Json(new { success = false, message = GetText("MaterialIssue", "MaterialInventoryNotFound") });

                return Json(new
                {
                    success = true,
                    data = dataList.Select(item => new
                    {
                        partNum = item.PartNum,
                        partDesc = item.PartDesc,
                        plant = item.Plant,
                        whseCode = item.WhseCode,
                        binNum = item.BinNum,
                        lotNumber = item.LotNumber,
                        qtyOnHand = item.QtyOnHand,
                        qtyAllocated = item.QtyAllocated,
                        availableQty = item.AvailableQty,
                        ium = item.IUM
                    })
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { success = false, message = GetText("MaterialIssue", "SessionExpired") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询物料库存信息出错");
                return Json(new { success = false, message = GetText("MaterialIssue", "QueryMaterialInventoryFailed") });
            }
        }
        #endregion
    }
}
