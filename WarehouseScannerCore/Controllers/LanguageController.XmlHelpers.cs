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
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>XML 读写辅助方法</summary>
    public partial class LanguageController
    {
        // 页面/键名白名单：只允许 ASCII 字母数字与 _ - .，
        // 防止用户提交的字符串被直接当作任意 XML 节点名写入。
        private static readonly Regex SafeXmlNamePattern = new(@"^[A-Za-z0-9_\-\.]+$", RegexOptions.Compiled);

        private static bool IsValidXmlName(string? value)
            => !string.IsNullOrEmpty(value) && SafeXmlNamePattern.IsMatch(value);

        // 获取所有页面列表
        private List<string> GetPageList()
        {
            var pages = new List<string>();

            if (System.IO.File.Exists(zhCnPath))
            {
                try
                {
                    var doc = XDocument.Load(zhCnPath);
                    var resources = doc.Descendants("Resources").FirstOrDefault();

                    if (resources != null)
                    {
                        // 从 <Resources> 节点下获取所有页面，排除系统页面
                        pages = resources.Elements()
                                  .Where(x => !IsSystemPage(x.Name.LocalName))
                                  .Select(x => x.Name.LocalName)
                                  .Where(x => !string.IsNullOrEmpty(x))
                                  .Distinct()
                                  .OrderBy(x => x)
                                  .ToList();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "读取XML文件失败");
                }
            }

            return pages;
        }

        // 判断是否为系统页面
        private bool IsSystemPage(string pageName)
        {
            var systemPages = new List<string> {
                "LanguageIndexTest",
                //"LanguageCreate",
                //"UserMenuPermissionIndex",
                //"UserMenuPermissionCreate",
                //"Home"
            };
            return systemPages.Contains(pageName);
        }

        // 获取所有语言项（与写侧共用同一把资源级锁，避免读到写入中途的文件）
        // 全链路异步：不在请求线程上同步等待锁（同步等待最长会阻塞 30 秒）
        private async Task<List<LanguageItem>> GetLanguageItemsAsync()
        {
            string traceId = _lockProvider.GetTraceId(null);
            SemaphoreSlim? langLock = await _lockProvider
                .TryAcquireLockAsync(LockPrefix, LockKey, traceId, "LanguageRead");
            try
            {
                return GetLanguageItemsCore();
            }
            finally
            {
                if (langLock != null)
                    _lockProvider.ReleaseLock(langLock, LockPrefix, LockKey, traceId);
            }
        }

        // 读取实现；调用方若已持有资源级写锁（如 Create 的重复检查），直接调用本方法避免自锁等待
        private List<LanguageItem> GetLanguageItemsCore()
        {
            var items = new List<LanguageItem>();

            try
            {
                // 读取中文XML文件
                if (System.IO.File.Exists(zhCnPath))
                {
                    var zhDoc = XDocument.Load(zhCnPath);
                    var zhResources = zhDoc.Descendants("Resources").FirstOrDefault();

                    if (zhResources != null)
                    {
                        // 读取英文XML文件
                        if (System.IO.File.Exists(enUsPath))
                        {
                            var enDoc = XDocument.Load(enUsPath);
                            var enResources = enDoc.Descendants("Resources").FirstOrDefault();

                            // 遍历所有页面
                            foreach (var pageElement in zhResources.Elements())
                            {
                                string pageName = pageElement.Name.LocalName;

                                // Home 页面也参与遍历（不排除）
                                // if (IsSystemPage(pageName))
                                //    continue;

                                // 遍历页面下的所有键值对
                                foreach (var keyElement in pageElement.Elements())
                                {
                                    string keyName = keyElement.Name.LocalName;
                                    string zhValue = keyElement.Value;

                                    // 查找对应的英文值
                                    string enValue = string.Empty;
                                    if (enResources != null)
                                    {
                                        var enPageElement = enResources.Elements(pageName).FirstOrDefault();
                                        if (enPageElement != null)
                                        {
                                            var enKeyElement = enPageElement.Elements(keyName).FirstOrDefault();
                                            if (enKeyElement != null)
                                            {
                                                enValue = enKeyElement.Value;
                                            }
                                        }
                                    }

                                    items.Add(new LanguageItem
                                    {
                                        Page = pageName,
                                        Key = keyName,
                                        ChineseValue = zhValue,
                                        EnglishValue = enValue
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "读取语言项失败");
            }

            return items.OrderBy(x => x.Page).ThenBy(x => x.Key).ToList();
        }

        // 保存到XML的辅助方法
        private bool SaveToXml(string filePath, string page, string key, string value, string languageCode)
        {
            if (!IsValidXmlName(page) || !IsValidXmlName(key))
            {
                _logger?.LogWarning("拒绝写入语言资源：页面或键名格式非法 Page:{Page} Key:{Key}", page, key);
                return false;
            }

            XDocument doc;

            if (System.IO.File.Exists(filePath))
            {
                doc = XDocument.Load(filePath);
            }
            else
            {
                // 创建新的XML文件
                doc = new XDocument(
                    new XElement("LanguageResources",
                        new XElement("Language",
                            new XElement("Code", languageCode),
                            new XElement("Name", languageCode == "zh-CN" ? "简体中文" : "English"),
                            new XElement("NativeName", languageCode == "zh-CN" ? "简体中文" : "English"),
                            new XElement("Resources")
                        )
                    )
                );
            }

            // 获取或创建 Resources 节点
            var languageElement = doc.Descendants("Language").FirstOrDefault();
            if (languageElement == null)
            {
                throw new InvalidOperationException("XML文件格式不正确");
            }

            var resourcesElement = languageElement.Element("Resources");
            if (resourcesElement == null)
            {
                resourcesElement = new XElement("Resources");
                languageElement.Add(resourcesElement);
            }

            // 检查是否已存在相同的页面
            var pageElement = resourcesElement.Elements(page).FirstOrDefault();
            if (pageElement == null)
            {
                pageElement = new XElement(page);
                resourcesElement.Add(pageElement);
            }

            // 检查是否已存在相同的键
            var existingItem = pageElement.Elements(key).FirstOrDefault();
            if (existingItem != null)
            {
                existingItem.Value = value;
            }
            else
            {
                pageElement.Add(new XElement(key, value));
            }

            // 原子写入（.tmp + File.Replace），避免崩溃时留下半截 XML
            AtomicFileWriter.WriteAtomic(filePath, doc.ToString());
            return true;
        }

        // 更新XML的辅助方法
        private void UpdateXml(string filePath, string page, string key, string value)
        {
            if (!IsValidXmlName(page) || !IsValidXmlName(key))
            {
                _logger?.LogWarning("拒绝更新语言资源：页面或键名格式非法 Page:{Page} Key:{Key}", page, key);
                throw new ArgumentException("页面或键名格式非法");
            }

            if (!System.IO.File.Exists(filePath))
            {
                throw new FileNotFoundException($"文件 {filePath} 不存在");
            }

            var doc = XDocument.Load(filePath);
            var resourcesElement = doc.Descendants("Resources").FirstOrDefault();
            if (resourcesElement == null)
            {
                throw new InvalidOperationException("XML文件格式不正确，未找到Resources节点");
            }

            var pageElement = resourcesElement.Elements(page).FirstOrDefault();
            if (pageElement == null)
            {
                throw new InvalidOperationException($"未找到页面 '{page}'");
            }

            var keyElement = pageElement.Elements(key).FirstOrDefault();
            if (keyElement != null)
            {
                keyElement.Value = value;
                // 原子写入，避免崩溃时留下半截 XML
                AtomicFileWriter.WriteAtomic(filePath, doc.ToString());
            }
            else
            {
                throw new InvalidOperationException($"未找到页面 '{page}' 的键 '{key}'");
            }
        }

        // 从XML删除的辅助方法
        private void DeleteFromXml(string filePath, string page, string key)
        {
            if (!IsValidXmlName(page) || !IsValidXmlName(key))
            {
                _logger?.LogWarning("拒绝删除语言资源：页面或键名格式非法 Page:{Page} Key:{Key}", page, key);
                throw new ArgumentException("页面或键名格式非法");
            }

            if (!System.IO.File.Exists(filePath))
            {
                return;
            }

            var doc = XDocument.Load(filePath);
            var resourcesElement = doc.Descendants("Resources").FirstOrDefault();
            if (resourcesElement == null)
            {
                return;
            }

            var pageElement = resourcesElement.Elements(page).FirstOrDefault();
            if (pageElement == null)
            {
                return;
            }

            var keyElement = pageElement.Elements(key).FirstOrDefault();
            if (keyElement != null)
            {
                keyElement.Remove();

                // 如果页面下没有元素了，删除页面节点
                if (!pageElement.HasElements)
                {
                    pageElement.Remove();
                }

                // 原子写入，避免崩溃时留下半截 XML
                AtomicFileWriter.WriteAtomic(filePath, doc.ToString());
            }
        }
    }
}