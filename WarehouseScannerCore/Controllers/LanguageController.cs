﻿﻿﻿﻿﻿﻿﻿using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    // 语言资源管理属于"系统设置"菜单
    [RequirePermission(MenuCodes.Settings)]
    // 继承 BaseController，统一复用基类的 _logger / _environment /
    // LoadLanguageResources / GetText / GetDefaultText / SetupViewBagLocalization。
    public partial class LanguageController : BaseController
    {
        private readonly string resourcesPath;
        private readonly string zhCnPath;
        private readonly string enUsPath;

        // 写操作并发锁前缀；两个 XML 文件（zh-CN/en-US）在同一次操作中先后全量重写，
        //     按实体（Page_Key）加锁无法阻止不同键交叉重写同一文件 → 资源级全局锁
        private const string LockPrefix = "Language";
        private const string LockKey = "LanguageResources";

        private readonly IBusinessLockProvider _lockProvider;

        // 说明：logger 仍声明为 ILogger<LanguageController>（ILogger<T> 派生自 ILogger），
        // 传入基类后日志类别保持为 LanguageController 不变。
        public LanguageController(
            IWebHostEnvironment environment,
            ILogger<LanguageController> logger,
            IDataProtectionProvider protectionProvider,
            IEpicorAuthServiceFactory epicorServiceFactory,
            IMemoryCache memoryCache,
            ILanguageService languageService,
            UserMenuPermissionService permissionService,
            IBusinessLockProvider lockProvider)
            : base(logger, environment, protectionProvider, epicorServiceFactory, memoryCache,
                   languageService, permissionService)
        {
            _lockProvider = lockProvider;

            // 文件在 Resources\Languages 文件夹中
            resourcesPath = Path.Combine(_environment.ContentRootPath, "Resources", "Languages");
            zhCnPath = Path.Combine(resourcesPath, "zh-CN.xml");
            enUsPath = Path.Combine(resourcesPath, "en-US.xml");
        }

        // 主页面
        // 修改主页面方法，添加分页参数
        public async Task<IActionResult> Index(string search = "", string searchType = "all", int page = 1, int pageSize = 20)
        {
            // 获取所有语言项
            var allItems = await GetLanguageItemsAsync();

            // 应用搜索过滤
            var filteredItems = allItems;

            if (!string.IsNullOrEmpty(search))
            {
                // 使用 ToLowerInvariant：ToLower() 依赖当前线程区域性，
                // 在土耳其语等区域下 'I' 会转成 'ı' 导致搜索匹配异常
                search = search.ToLowerInvariant();

                switch ((searchType ?? "all").ToLowerInvariant())
                {
                    case "page":
                        filteredItems = allItems.Where(x => x.Page.ToLowerInvariant().Contains(search)).ToList();
                        break;
                    case "key":
                        filteredItems = allItems.Where(x => x.Key.ToLowerInvariant().Contains(search)).ToList();
                        break;
                    case "chinese":
                        filteredItems = allItems.Where(x => x.ChineseValue.ToLowerInvariant().Contains(search)).ToList();
                        break;
                    case "english":
                        filteredItems = allItems.Where(x => x.EnglishValue.ToLowerInvariant().Contains(search)).ToList();
                        break;
                    default: // all
                        filteredItems = allItems.Where(x =>
                            x.Page.ToLowerInvariant().Contains(search) ||
                            x.Key.ToLowerInvariant().Contains(search) ||
                            x.ChineseValue.ToLowerInvariant().Contains(search) ||
                            x.EnglishValue.ToLowerInvariant().Contains(search)
                        ).ToList();
                        break;
                }
            }

            // pageSize 直接来自查询字符串，必须夹紧到合理区间。
            //   无下限：pageSize<=0 使 (int)Math.Ceiling(...) 得到非正数，页码被强行拉回 1，
            //     Take(非正数) 返回空列表 —— 页面表现为"数据全部消失"；
            //   无上限：等于绕过分页上限，一次性拉取全量数据。
            //   注意这里是浮点除法，不会抛 DivideByZeroException，故危害是"分页失效"而非崩溃。
            pageSize = Math.Clamp(pageSize, 1, 100);

            // 计算分页
            var totalItems = filteredItems.Count;
            var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);

            // 确保页码在有效范围内（结果集为空时固定为第 1 页）
            page = totalPages == 0 ? 1 : Math.Max(1, Math.Min(page, totalPages));

            // 获取当前页的数据
            var pagedItems = filteredItems
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var pageList = GetPageList();
            ViewBag.PageList = new SelectList(pageList);

            // 传递分页信息到视图
            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = totalPages;
            ViewBag.SearchTerm = search;
            ViewBag.SearchType = searchType;

            // 传递本地化函数和当前文化信息
            // 统一设置 ViewBag 本地化/用户信息（基类实现）
            SetupViewBagLocalization();

            // 设置页面标题
            ViewData["Title"] = GetText("LanguageIndex", "Title");

            return View(pagedItems);
        }
        // 创建页面
        [HttpGet]
        public IActionResult Create()
        {
            ViewBag.PageList = new SelectList(GetPageList());

            // 传递本地化函数和当前文化信息
            // 统一设置 ViewBag 本地化/用户信息（基类实现）
            SetupViewBagLocalization();

            // 设置页面标题
            ViewData["Title"] = GetText("LanguageCreate", "CreateLanguageItem");

            return View(new LanguageItem());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(LanguageItem model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    // 重复检查（读 XML）+ 双文件写入（全量重写）整体取资源级锁，
                    //     防止并发创建绕过重复校验或交叉重写同一 XML 文件
                    string traceId = _lockProvider.GetTraceId(HttpContext);
                    SemaphoreSlim? langLock = await _lockProvider.TryAcquireLockAsync(
                        LockPrefix, LockKey, traceId, GetCurrentUserId());
                    if (langLock == null)
                    {
                        ModelState.AddModelError("", GetText("LanguageIndex", "SystemBusy"));
                        ViewBag.PageList = new SelectList(GetPageList());
                        // 统一设置 ViewBag 本地化/用户信息（基类实现）
                        SetupViewBagLocalization();
                        return View(model);
                    }
                    try
                    {
                        // 检查是否已存在相同的页面和键名
                        // 已持有资源级写锁，直接走无锁读实现，避免对同一信号量自锁等待
                        var existingItems = GetLanguageItemsCore();
                        if (existingItems.Any(x => x.Page == model.Page && x.Key == model.Key))
                        {
                            ModelState.AddModelError("", GetText("LanguageCreate", "DuplicateError"));
                            ViewBag.PageList = new SelectList(GetPageList());
                            // 统一设置 ViewBag 本地化/用户信息（基类实现）
                            SetupViewBagLocalization();
                            return View(model);
                        }

                        // 确保Resources/Languages文件夹存在
                        if (!Directory.Exists(resourcesPath))
                        {
                            Directory.CreateDirectory(resourcesPath);
                        }

                        // 保存到zh-CN.xml（先记录原内容，英文写失败时回滚，避免只更新一半）
                        string? zhOriginal = System.IO.File.Exists(zhCnPath) ? System.IO.File.ReadAllText(zhCnPath) : null;
                        if (!SaveToXml(zhCnPath, model.Page, model.Key, model.ChineseValue, "zh-CN"))
                        {
                            _logger?.LogWarning("保存语言项被拒绝：页面或键名格式非法 Page:{Page} Key:{Key}",
                                model.Page, model.Key);
                            ModelState.AddModelError("", GetText("LanguageIndex", "SaveFailed"));
                            ViewBag.PageList = new SelectList(GetPageList());
                            // 统一设置 ViewBag 本地化/用户信息（基类实现）
                            SetupViewBagLocalization();
                            return View(model);
                        }

                        // 保存到en-US.xml；失败则回滚中文文件，绝不留下半套资源
                        try
                        {
                            if (!SaveToXml(enUsPath, model.Page, model.Key, model.EnglishValue, "en-US"))
                                throw new InvalidOperationException("写入英文语言文件失败");
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                if (zhOriginal == null)
                                {
                                    if (System.IO.File.Exists(zhCnPath)) System.IO.File.Delete(zhCnPath);
                                }
                                else
                                {
                                    AtomicFileWriter.WriteAtomic(zhCnPath, zhOriginal);
                                }
                            }
                            catch (Exception rollbackEx)
                            {
                                _logger?.LogError(rollbackEx, "回滚中文语言文件失败 Page:{Page} Key:{Key}",
                                    model.Page, model.Key);
                            }

                            _logger?.LogError(ex, "写入英文语言文件失败，已回滚中文文件 Page:{Page} Key:{Key}",
                                model.Page, model.Key);
                            ModelState.AddModelError("", GetText("LanguageIndex", "SaveFailed"));
                            ViewBag.PageList = new SelectList(GetPageList());
                            // 统一设置 ViewBag 本地化/用户信息（基类实现）
                            SetupViewBagLocalization();
                            return View(model);
                        }

                        // 写 XML 成功后立即失效语言缓存，否则生产环境 TTL 60 分钟内
                        //     所有用户仍读取旧文案（ClearCache 原先已实现但全项目零调用）
                        _languageService?.ClearCache();

                        TempData["SuccessMessage"] = GetText("LanguageIndex", "SaveSuccess");
                        return RedirectToAction("Index");
                    }
                    finally
                    {
                        _lockProvider.ReleaseLock(langLock, LockPrefix, LockKey, traceId);
                    }
                }
                catch (Exception ex)
                {
                    // 不透传 ex.Message（内部信息），只记服务端日志
                    _logger?.LogError(ex, "保存语言项失败 Page:{Page} Key:{Key}", model.Page, model.Key);
                    ModelState.AddModelError("", GetText("LanguageIndex", "SaveFailed"));
                }
            }

            ViewBag.PageList = new SelectList(GetPageList());
            // 统一设置 ViewBag 本地化/用户信息（基类实现）
            SetupViewBagLocalization();
            return View(model);
        }

        // 编辑页面
        [HttpGet]
        public async Task<IActionResult> Edit(string page, string key)
        {
            var items = await GetLanguageItemsAsync();
            var item = items.FirstOrDefault(x => x.Page == page && x.Key == key);

            if (item == null)
            {
                return NotFound();
            }

            ViewBag.PageList = new SelectList(GetPageList());
            // 统一设置 ViewBag 本地化/用户信息（基类实现）
            SetupViewBagLocalization();

            // 设置页面标题 - 修改为 LanguageEdit
            ViewData["Title"] = GetText("LanguageEdit", "EditLanguageItem");

            return View(item);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(LanguageItem model)
        {
            if (ModelState.IsValid)
            {

                try
                {
                    // 双 XML 文件更新（全量重写）取资源级锁串行化
                    string traceId = _lockProvider.GetTraceId(HttpContext);
                    SemaphoreSlim? langLock = await _lockProvider.TryAcquireLockAsync(
                        LockPrefix, LockKey, traceId, GetCurrentUserId());
                    if (langLock == null)
                    {
                        ModelState.AddModelError("", GetText("LanguageEdit", "SystemBusy"));
                        ViewBag.PageList = new SelectList(GetPageList());
                        // 统一设置 ViewBag 本地化/用户信息（基类实现）
                        SetupViewBagLocalization();
                        return View(model);
                    }
                    try
                    {
                        // 更新zh-CN.xml
                        UpdateXml(zhCnPath, model.Page, model.Key, model.ChineseValue);

                        // 更新en-US.xml
                        UpdateXml(enUsPath, model.Page, model.Key, model.EnglishValue);

                        // 写 XML 成功后立即失效语言缓存（原因同 Create）
                        _languageService?.ClearCache();

                        // 修改为使用 LanguageEdit
                        TempData["SuccessMessage"] = GetText("LanguageEdit", "SaveSuccess");
                        return RedirectToAction("Index");
                    }
                    finally
                    {
                        _lockProvider.ReleaseLock(langLock, LockPrefix, LockKey, traceId);
                    }
                }
                catch (Exception ex)
                {
                    // 修改为使用 LanguageEdit
                    // 不透传 ex.Message（内部信息），只记服务端日志
                    _logger?.LogError(ex, "更新语言项失败 Page:{Page} Key:{Key}", model.Page, model.Key);
                    ModelState.AddModelError("", GetText("LanguageEdit", "SaveFailed"));
                }
            }

            ViewBag.PageList = new SelectList(GetPageList());
            // 统一设置 ViewBag 本地化/用户信息（基类实现）
            SetupViewBagLocalization();
            return View(model);
        }

        // 删除
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string page, string key)
        {
            // 双 XML 文件删除（全量重写）取资源级锁串行化
            string traceId = _lockProvider.GetTraceId(HttpContext);
            SemaphoreSlim? langLock = await _lockProvider.TryAcquireLockAsync(
                LockPrefix, LockKey, traceId, GetCurrentUserId());
            if (langLock == null)
            {
                TempData["ErrorMessage"] = GetText("LanguageIndex", "SystemBusy");
                return RedirectToAction("Index");
            }
            try
            {
                // 从zh-CN.xml删除
                DeleteFromXml(zhCnPath, page, key);

                // 从en-US.xml删除
                DeleteFromXml(enUsPath, page, key);

                // 写 XML 成功后立即失效语言缓存（原因同 Create）
                _languageService?.ClearCache();

                TempData["SuccessMessage"] = GetText("LanguageIndex", "DeleteSuccess");
            }
            catch (Exception ex)
            {
                // 不透传 ex.Message（内部信息），只记服务端日志
                _logger?.LogError(ex, "删除语言项失败 Page:{Page} Key:{Key}", page, key);
                TempData["ErrorMessage"] = GetText("LanguageIndex", "DeleteFailed");
            }
            finally
            {
                _lockProvider.ReleaseLock(langLock, LockPrefix, LockKey, traceId);
            }

            return RedirectToAction("Index");
        }

    }
}