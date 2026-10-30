using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    // 系统配置页属于"系统设置"菜单
    [RequirePermission(MenuCodes.Settings)]
    // 继承 BaseController，统一复用基类的 _logger / _environment /
    // LoadLanguageResources / IsUserAuthenticated / GetText / SetupViewBagLocalization。
    public class ConfigController : BaseController
    {
        /// <summary>
        /// 敏感值掩码。
        /// 连接字符串内含数据库账号/密码，与 FactoryController 同款处理：
        /// 页面只回显掩码，提交时保持掩码或留空即视为"不修改，沿用磁盘原值"。
        /// </summary>
        private const string MaskedSecret = "***";

        // 写操作并发锁前缀（与既有 LockPrefix 常量命名一致：POReceipt/Outbound/.../Config）
        private const string LockPrefix = "Config";

        private readonly ConfigService _configService;
        private readonly IBusinessLockProvider _lockProvider;

        // 说明：logger 仍声明为 ILogger<ConfigController>（ILogger<T> 派生自 ILogger），
        // 传入基类后日志类别保持为 ConfigController 不变。
        public ConfigController(
            ILogger<ConfigController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IMemoryCache memoryCache,
            ConfigService configService,
            ILanguageService languageService,
            UserMenuPermissionService permissionService,
            IBusinessLockProvider lockProvider)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache,
                   languageService, permissionService)
        {
            _configService = configService;
            _lockProvider = lockProvider;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            try
            {
                // 使用Cookie检查用户是否已登录
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问配置页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                // 加载数据库配置
                var config = await _configService.LoadConfigAsync();

                // LoadConfigAsync 返回的 ConnectionString 是解密后的明文（内含数据库账号/密码），
                //       必须在交给视图前替换为掩码，否则会原样渲染进 Views/Config/Index.cshtml 的 textarea。
                //       与 Save 的取值规则配合：保持 *** 或留空 → 沿用磁盘上的原加密值。
                if (!string.IsNullOrEmpty(config.ConnectionString))
                {
                    config.ConnectionString = MaskedSecret;
                }

                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View(config);
            }
            catch (Exception ex)
            {
                // 不向外透传 ex.Message（可能含连接串/密码等内部信息），仅保留服务端日志
                _logger.LogError(ex, "访问配置页面时发生错误");
                TempData["ErrorMessage"] = GetText("DatabaseConfig", "Error");
                return RedirectToAction("Index", "Home");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(DatabaseConfig config)
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    return Json(new { success = false, message = "User not authenticated" });
                }

                if (config == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = GetText("DatabaseConfig", "ConfigSaveFailed")
                    });
                }

                // 数据库配置是单文件 Load→改→Save 全量重写，并发保存会互相覆盖。
                //     用资源级锁串行化（IBusinessLockProvider，与业务控制器 Adjustment/Transfer 同款范式）；
                //     配置文件仅一份，锁键取固定资源名即可。
                string traceId = _lockProvider.GetTraceId(HttpContext);
                SemaphoreSlim? configLock = await _lockProvider.TryAcquireLockAsync(
                    LockPrefix, "DatabaseConfig", traceId, GetCurrentUserId());
                if (configLock == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = GetText("DatabaseConfig", "SystemBusy")
                    });
                }

                try
                {
                    // 页面回显的是掩码。保持掩码或留空 → 沿用磁盘上已存储的连接串，
                    //       绝不能把 "***" 当成真实连接串去加密覆盖，否则会把配置写坏。
                    if (string.IsNullOrWhiteSpace(config.ConnectionString)
                        || config.ConnectionString == MaskedSecret)
                    {
                        var existing = await _configService.LoadConfigAsync();
                        if (string.IsNullOrEmpty(existing.ConnectionString))
                        {
                            return Json(new
                            {
                                success = false,
                                message = GetText("DatabaseConfig", "ConnectionStringRequired")
                            });
                        }

                        // existing.ConnectionString 为磁盘值解密后的明文，交给 SaveConfigAsync 重新加密落盘
                        config.ConnectionString = existing.ConnectionString;
                    }

                    bool success = await _configService.SaveConfigAsync(config);

                    if (success)
                    {
                        _logger.LogInformation("数据库配置保存成功");
                        return Json(new
                        {
                            success = true,
                            message = GetText("DatabaseConfig", "ConfigSaved")
                        });
                    }
                    else
                    {
                        return Json(new
                        {
                            success = false,
                            message = GetText("DatabaseConfig", "ConfigSaveFailed")
                        });
                    }
                }
                finally
                {
                    _lockProvider.ReleaseLock(configLock, LockPrefix, "DatabaseConfig", traceId);
                }
            }
            catch (Exception ex)
            {
                // 不向外透传 ex.Message（连接串相关异常常带敏感信息），仅保留服务端日志
                _logger.LogError(ex, "保存数据库配置时发生错误");
                return Json(new
                {
                    success = false,
                    message = GetText("DatabaseConfig", "ConfigSaveFailed")
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Test([FromBody] DatabaseConfig config)
        {
            try
            {
                if (!IsUserAuthenticated())
                {
                    return Json(new { success = false, message = "User not authenticated" });
                }

                string connectionString = config?.ConnectionString ?? "";

                // 页面回显的是掩码。用户未改动连接串就直接点"测试连接"时，
                //       用磁盘上已存储的连接串来测试，保持原有操作流程可用。
                if (string.IsNullOrWhiteSpace(connectionString) || connectionString == MaskedSecret)
                {
                    var existing = await _configService.LoadConfigAsync();
                    connectionString = existing.ConnectionString ?? "";
                }

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    return Json(new
                    {
                        success = false,
                        message = "连接字符串为空，请先填写",
                        testPassed = false
                    });
                }

                // 安全：测试接口只允许连接当前已保存配置的数据库服务器，
                //       否则任意连接串都能触发 TCP 连接，等同端口扫描探针。
                var existingConfig = await _configService.LoadConfigAsync();
                string savedDataSource = string.Empty;
                if (!string.IsNullOrWhiteSpace(existingConfig.ConnectionString))
                {
                    try
                    {
                        savedDataSource = new SqlConnectionStringBuilder(existingConfig.ConnectionString).DataSource ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "已保存的数据库连接字符串解析失败，无法校验目标服务器");
                    }
                }

                string requestedDataSource;
                try
                {
                    requestedDataSource = new SqlConnectionStringBuilder(connectionString).DataSource ?? string.Empty;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "请求的数据库连接字符串解析失败");
                    return Json(new
                    {
                        success = false,
                        message = GetText("DatabaseConfig", "ConnectionTestDataSourceMismatch"),
                        testPassed = false
                    });
                }

                // 首次安装（尚未保存任何连接串）时放行，否则管理员无法完成初始配置；
                // 一旦已保存目标服务器，后续测试只允许连接同一服务器。
                if (!string.IsNullOrWhiteSpace(savedDataSource)
                    && !string.Equals(requestedDataSource.Trim(), savedDataSource.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("拒绝测试非当前配置的数据库服务器: Requested={Requested}, Saved={Saved}",
                        requestedDataSource, savedDataSource);
                    return Json(new
                    {
                        success = false,
                        message = GetText("DatabaseConfig", "ConnectionTestDataSourceMismatch"),
                        testPassed = false
                    });
                }

                // 测试连接
                bool isConnected = false;
                string testMessage = "";

                try
                {
                    isConnected = _configService.TestConnection(connectionString);
                }
                catch (Exception ex)
                {
                    // 不把异常原文回传前端（可能含服务器名/账号等内部信息），仅记录服务端日志；
                    // 但仍给出可读的失败原因，避免前端只显示一个无信息量的空消息。
                    _logger.LogError(ex, "测试连接时发生异常");
                    testMessage = GetText("DatabaseConfig", "ConnectionTestException");
                }

                if (isConnected)
                {
                    _logger.LogInformation("数据库连接测试通过");
                    return Json(new
                    {
                        success = true,
                        message = GetText("DatabaseConfig", "ConnectionTestPassed"),
                        testPassed = true
                    });
                }
                else
                {
                    string errorMsg = string.IsNullOrEmpty(testMessage)
                        ? GetText("DatabaseConfig", "ConnectionTestFailed")
                        : $"{GetText("DatabaseConfig", "ConnectionTestFailed")}: {testMessage}";

                    return Json(new
                    {
                        success = false,
                        message = errorMsg,
                        testPassed = false
                    });
                }
            }
            catch (Exception ex)
            {
                // 不向外透传 ex.Message，统一使用本地化提示
                _logger.LogError(ex, "测试数据库连接时发生错误");
                return Json(new
                {
                    success = false,
                    message = GetText("DatabaseConfig", "TestError"),
                    testPassed = false
                });
            }
        }

    }
}