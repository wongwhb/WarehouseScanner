using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
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
    // 站点管理属于"系统设置"菜单
    [RequirePermission(MenuCodes.Settings)]
    // 继承 BaseController，统一复用基类的 _logger / _environment /
    // LoadLanguageResources / IsUserAuthenticated / GetText / GetDefaultText / SetupViewBagLocalization。
    public class FactoryController : BaseController
    {
        /// <summary>
        /// 敏感凭证掩码。
        /// 页面回显时用掩码替代明文；用户保持掩码或留空即视为"不修改，保留原值"。
        /// </summary>
        private const string MaskedSecret = "***";

        /// <summary>掩码时保留的首尾可见字符数，便于管理员辨识是否为同一密钥</summary>
        private const int MaskKeepChars = 4;

        // 写操作并发锁前缀（与既有 LockPrefix 常量命名一致）
        private const string LockPrefix = "Factory";
        // 站点配置是单文件全量重写，不同站点的并发增删改仍会竞争同一文件 → 资源级全局锁
        private const string LockKey = "FactoryConfig";

        private readonly FactoryConfigService _factoryService;
        private readonly EncryptionHelper _encryption;
        private readonly IBusinessLockProvider _lockProvider;

        // 说明：logger 仍声明为 ILogger<FactoryController>（ILogger<T> 派生自 ILogger），
        // 传入基类后日志类别保持为 FactoryController 不变。
        public FactoryController(
            FactoryConfigService factoryService,
            ILogger<FactoryController> logger,
            IWebHostEnvironment environment,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IMemoryCache memoryCache,
            EncryptionHelper encryption,
            ILanguageService languageService,
            UserMenuPermissionService permissionService,
            IBusinessLockProvider lockProvider)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache,
                   languageService, permissionService)
        {
            _factoryService = factoryService;
            _encryption = encryption;
            _lockProvider = lockProvider;
        }

        /// <summary>
        /// 生成掩码：保留首尾各若干字符，中间用 *** 替代。
        /// 过短的密钥不保留任何字符，避免掩码后退化成明文。
        /// </summary>
        private static string MaskSecretValue(string? secret)
        {
            if (string.IsNullOrEmpty(secret)) return string.Empty;
            if (secret.Length <= MaskKeepChars * 2) return MaskedSecret;
            return secret.Substring(0, MaskKeepChars) + MaskedSecret + secret.Substring(secret.Length - MaskKeepChars);
        }

        /// <summary>
        /// 对站点模型中的敏感字段做掩码处理（禁止 ApiKey 明文回显到前端）。
        /// 仅对非空值打码，空值保持为空，避免误导用户以为已配置凭证。
        /// </summary>
        private static void MaskSecrets(Factory factory)
        {
            if (factory == null) return;
            if (!string.IsNullOrEmpty(factory.ApiKey)) factory.ApiKey = MaskSecretValue(factory.ApiKey);
        }

        // GET: Factory
        public async Task<IActionResult> Index()
        {
            try
            {
                // 使用Cookie检查用户是否已登录
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问工厂管理页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                // 本地化文本统一走基类 GetText / GetDefaultText（避免各页面重复的语言解析与硬编码字典）

                // 获取工厂数据
                var config = await _factoryService.LoadFactoriesAsync();

                // 统一设置 ViewBag 本地化/用户信息（基类实现，用户信息来自 Session）
                // 注：禁止额外从 Cookie 读取 UserId/UserRole/SiteName 覆盖上述赋值——
                //     这三个 Cookie 服务端从未写入（恒为 null），且可被客户端伪造。
                SetupViewBagLocalization();

                // 列表页同样脱敏，作为纵深防御（避免任何情况下明文密钥进入视图模型）
                if (config.Factories != null)
                {
                    foreach (var f in config.Factories)
                    {
                        MaskSecrets(f);
                    }
                }

                return View(config.Factories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载工厂列表时发生错误");
                TempData["ErrorMessage"] = "加载工厂列表时发生错误";
                return View(new List<Factory>());
            }
        }

        // GET: Factory/Create
        [HttpGet]
        public IActionResult Create()
        {
            try
            {
                // 使用Cookie检查用户是否已登录
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问创建工厂页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                // 本地化文本统一走基类 GetText / GetDefaultText（避免各页面重复的语言解析与硬编码字典）

                // 将本地化函数传递给View
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问创建工厂页面时发生错误");
                return RedirectToAction(nameof(Index));
            }
        }

        // POST: Factory/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Factory factory)
        {
            try
            {
                // 使用Cookie检查用户是否已登录
                if (!IsUserAuthenticated())
                {
                    return RedirectToAction("Login", "Account");
                }

                // 本地化文本统一走基类 GetText / GetDefaultText（避免各页面重复的语言解析与硬编码字典）

                // 新建站点时必须提供 ApiKey（在此显式校验，不依赖模型 [Required]）
                if (string.IsNullOrWhiteSpace(factory.ApiKey))
                {
                    ModelState.AddModelError("ApiKey", "ApiKey 不能为空");
                }

                if (ModelState.IsValid)
                {
                    // 站点配置为单文件全量重写，写入前取资源级锁串行化（含重复代码判断在内的
                    //     读-判-写序列整体处于锁内，防止并发创建绕过重复校验）
                    string traceId = _lockProvider.GetTraceId(HttpContext);
                    SemaphoreSlim? factoryLock = await _lockProvider.TryAcquireLockAsync(
                        LockPrefix, LockKey, traceId, GetCurrentUserId());
                    if (factoryLock == null)
                    {
                        TempData["ErrorMessage"] = GetText("FactoryManagement", "SystemBusy");
                        return RedirectToAction(nameof(Index));
                    }
                    try
                    {
                        // 保存前加密敏感字段（保留原文以便失败时回显；此处为用户本次输入，非服务端解密回显）
                        string plainApiKey = factory.ApiKey ?? "";
                        factory.ApiKey = _encryption.Encrypt(plainApiKey);

                        bool added = await _factoryService.AddFactoryAsync(factory);

                        if (added)
                        {
                            TempData["SuccessMessage"] = GetText("FactoryManagement", "CreateSuccess");
                            return RedirectToAction(nameof(Index));
                        }

                        // 保存失败：区分 代码重复 / 公司+工厂组合重复（权威校验在服务层锁内，此处仅取提示文案）
                        factory.ApiKey = plainApiKey;
                        bool codeExists = await _factoryService.GetFactoryByCodeAsync(factory.Code) != null;
                        ModelState.AddModelError(
                            codeExists ? "Code" : "Company",
                            GetText("FactoryManagement", codeExists ? "DuplicateCode" : "DuplicateCompanyPlant"));
                    }
                    finally
                    {
                        _lockProvider.ReleaseLock(factoryLock, LockPrefix, LockKey, traceId);
                    }
                }

                // 如果验证失败，重新设置ViewBag
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View(factory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建工厂时发生错误");
                TempData["ErrorMessage"] = GetDefaultText("FactoryManagement", "ErrorOccurred", "zh-CN");
                return View(factory);
            }
        }

        // GET: Factory/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            try
            {
                // 使用Cookie检查用户是否已登录
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问编辑工厂页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                if (string.IsNullOrEmpty(code))
                {
                    TempData["ErrorMessage"] = "站点代码不能为空";
                    return RedirectToAction(nameof(Index));
                }

                // 本地化文本统一走基类 GetText / GetDefaultText（避免各页面重复的语言解析与硬编码字典）

                var factory = await _factoryService.GetFactoryByCodeAsync(code);
                if (factory == null)
                {
                    TempData["ErrorMessage"] = GetText("FactoryManagement", "NotFound");
                    return RedirectToAction(nameof(Index));
                }

                // GetFactoryByCodeAsync 返回的是「解密后的明文」副本。
                // 安全：ApiKey 绝不明文回显到前端，统一走掩码（保留首尾少量字符便于辨识）；
                //      POST 时保持掩码或留空即视为「不修改」，服务端保留磁盘原密文。

                // 保存原始代码用于更新
                ViewData["OriginalCode"] = factory.Code;

                MaskSecrets(factory);

                // 将本地化函数传递给View
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View(factory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问编辑工厂页面时发生错误");
                TempData["ErrorMessage"] = "访问编辑页面时发生错误";
                return RedirectToAction(nameof(Index));
            }
        }

        // POST: Factory/Edit/{code}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string originalCode, Factory factory)
        {
            try
            {
                // 使用Cookie检查用户是否已登录
                if (!IsUserAuthenticated())
                {
                    return RedirectToAction("Login", "Account");
                }

                // 本地化文本统一走基类 GetText / GetDefaultText（避免各页面重复的语言解析与硬编码字典）

                if (ModelState.IsValid)
                {
                    // 掩码回填（读磁盘原值）+ 更新（全量重写文件）整体取锁，
                    //     防止并发编辑同一或不同站点时互相覆盖
                    string traceId = _lockProvider.GetTraceId(HttpContext);
                    SemaphoreSlim? factoryLock = await _lockProvider.TryAcquireLockAsync(
                        LockPrefix, LockKey, traceId, GetCurrentUserId());
                    if (factoryLock == null)
                    {
                        TempData["ErrorMessage"] = GetText("FactoryManagement", "SystemBusy");
                        return RedirectToAction(nameof(Index));
                    }
                    try
                    {
                        // 保存前加密敏感字段：
                        //   - ApiKey：编辑页回显的是掩码（首尾少量字符 + ***），不是明文。
                        //     留空 / 提交值仍是掩码 → 视为「不修改」，取磁盘原加密值（防止掩码串被当新密钥加密而破坏凭据）；
                        //     只有提交值与掩码不同且非空时，才按明文重新加密入库。
                        //     若用户误把密文（v2:...）粘进文本框，识别后直接保留，避免双重加密导致 Key 永久损坏。
                        string plainApiKey = factory.ApiKey ?? "";
                        bool keepExistingApiKey = string.IsNullOrWhiteSpace(plainApiKey)
                            || plainApiKey.Contains(MaskedSecret, StringComparison.Ordinal);

                        if (keepExistingApiKey)
                        {
                            // 留空 / 保持掩码 → 取磁盘上的原始（加密）ApiKey，不改动
                            factory.ApiKey = _factoryService.GetRawApiKey(originalCode) ?? "";
                        }
                        else if (plainApiKey.StartsWith("v2:", StringComparison.Ordinal)
                                 && _encryption.TryDecrypt(plainApiKey) != plainApiKey)
                        {
                            // 提交值带 v2: 前缀且能解开 → 已是密文，原样保存，不二次加密。
                            // 必须同时校验前缀：真实明文 Key 本身可能是合法 Base64，
                            // 单靠 TryDecrypt 结果判断存在约 0.4% 的概率把明文误认成密文而漏加密；
                            // 而明文 Key 以 "v2:" 开头但又解不开时 TryDecrypt 原样返回，仍会走到加密分支。
                            factory.ApiKey = plainApiKey;
                        }
                        else
                        {
                            factory.ApiKey = _encryption.Encrypt(plainApiKey);
                        }

                        bool updated = await _factoryService.UpdateFactoryAsync(originalCode, factory);

                        if (updated)
                        {
                            TempData["SuccessMessage"] = GetText("FactoryManagement", "UpdateSuccess");
                            return RedirectToAction(nameof(Index));
                        }

                        // 保存失败：ApiKey 仍回显用户输入，但下方统一掩码后再进视图（禁止明文进 HTML）
                        factory.ApiKey = plainApiKey;
                        // 区分 站点不存在 / 公司+工厂组合重复
                        bool stillExists = await _factoryService.GetFactoryByCodeAsync(originalCode) != null;
                        TempData["ErrorMessage"] = GetText("FactoryManagement",
                            stillExists ? "DuplicateCompanyPlant" : "UpdateFailed");
                    }
                    finally
                    {
                        _lockProvider.ReleaseLock(factoryLock, LockPrefix, LockKey, traceId);
                    }
                }

                // 如果验证失败，重新设置ViewBag
                ViewData["OriginalCode"] = originalCode;
                // 回显前再次掩码：无论提交校验失败还是保存失败，都不得把明文/密文写入 HTML
                MaskSecrets(factory);
                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View(factory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新工厂时发生错误");
                TempData["ErrorMessage"] = GetDefaultText("FactoryManagement", "ErrorOccurred", "zh-CN");
                MaskSecrets(factory);
                return View(factory);
            }
        }

        // GET: Factory/Delete/{code}
        // 显式声明 HTTP 方法：与下方 [HttpPost, ActionName("Delete")] 依赖方法约束消歧，
        // 避免后人补特性时把 GET 请求路由到 POST 重载（或反之）。
        [HttpGet]
        public async Task<IActionResult> Delete(string code)
        {
            try
            {
                // 使用Cookie检查用户是否已登录
                if (!IsUserAuthenticated())
                {
                    _logger.LogWarning("未登录用户尝试访问删除工厂页面，重定向到登录页");
                    return RedirectToAction("Login", "Account");
                }

                if (string.IsNullOrEmpty(code))
                {
                    TempData["ErrorMessage"] = "站点代码不能为空";
                    return RedirectToAction(nameof(Index));
                }

                // 本地化文本统一走基类 GetText / GetDefaultText（避免各页面重复的语言解析与硬编码字典）

                var factory = await _factoryService.GetFactoryByCodeAsync(code);
                if (factory == null)
                {
                    TempData["ErrorMessage"] = GetText("FactoryManagement", "NotFound");
                    return RedirectToAction(nameof(Index));
                }

                // 确认删除页同样不暴露明文凭证
                MaskSecrets(factory);

                // 统一设置 ViewBag 本地化/用户信息（基类实现）
                SetupViewBagLocalization();

                return View(factory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "访问删除工厂页面时发生错误");
                TempData["ErrorMessage"] = "访问删除页面时发生错误";
                return RedirectToAction(nameof(Index));
            }
        }

        // POST: Factory/Delete/{code}
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            try
            {
                // 使用Cookie检查用户是否已登录
                if (!IsUserAuthenticated())
                {
                    return RedirectToAction("Login", "Account");
                }

                // 本地化文本统一走基类 GetText / GetDefaultText

                // 删除同样全量重写站点配置文件，与增/改共用同一把资源级锁
                string traceId = _lockProvider.GetTraceId(HttpContext);
                SemaphoreSlim? factoryLock = await _lockProvider.TryAcquireLockAsync(
                    LockPrefix, LockKey, traceId, GetCurrentUserId());
                if (factoryLock == null)
                {
                    TempData["ErrorMessage"] = GetText("FactoryManagement", "SystemBusy");
                    return RedirectToAction(nameof(Index));
                }
                try
                {
                    if (await _factoryService.DeleteFactoryAsync(code))
                    {
                        TempData["SuccessMessage"] = GetText("FactoryManagement", "DeleteSuccess");
                    }
                    else
                    {
                        TempData["ErrorMessage"] = GetText("FactoryManagement", "DeleteFailed");
                    }
                }
                finally
                {
                    _lockProvider.ReleaseLock(factoryLock, LockPrefix, LockKey, traceId);
                }

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除工厂时发生错误");
                TempData["ErrorMessage"] = "删除工厂时发生错误";
                return RedirectToAction(nameof(Index));
            }
        }

    }
}