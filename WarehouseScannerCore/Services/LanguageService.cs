// Services/LanguageService.cs
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;  // 添加这个
using System.Xml;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using System.Net;  // WebUtility.HtmlEncode

namespace WarehouseScannerCore.Services  // 修正命名空间
{
    public interface ILanguageService
    {
        string GetString(string languageCode, string category, string key);
        /// <summary>
        /// 基于资源存在性的查找：命中返回 true 并输出文本；未命中返回 false（仅写服务端日志）。
        /// 调用方据此替代 "[section.key]" 字符串形状判断，避免把以 '[' 开头的合法文案误判为缺失。
        /// </summary>
        bool TryGetString(string languageCode, string category, string key, out string value);
        /// <summary>
        /// 获取<b>未做 HTML 编码</b>的原文：专供 JSON API / 非 HTML 上下文（业务消息）。
        /// 警告：禁止用于视图的 @Html.Raw 渲染——加载期 HtmlEncode 是存储型 XSS 的唯一防线，
        /// 原文出口一旦进入 HTML 上下文即重新打开攻击链。
        /// </summary>
        bool TryGetRawString(string languageCode, string category, string key, out string value);
        Dictionary<string, string> GetCategoryStrings(string languageCode, string category);
        Dictionary<string, Dictionary<string, string>> GetLanguageResources(string languageCode);
        List<LanguageInfo> GetSupportedLanguages();
        bool ValidateLanguage(string languageCode);
        string GetDefaultLanguage();
        void ClearCache();
    }

    public class LanguageService : ILanguageService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<LanguageService> _logger;
        private readonly IWebHostEnvironment _environment;
        private const string LanguagesFolder = "Resources/Languages";
        private const string LanguageConfigFile = "LanguageConfig.xml";
        private const int CacheDurationMinutes = 60;
        private const int DevCacheDurationSeconds = 10;

        public LanguageService(
            IMemoryCache cache,
            ILogger<LanguageService> logger,
            IWebHostEnvironment environment)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        }

        public string GetString(string languageCode, string category, string key)
        {
            try
            {
                languageCode = ValidateAndNormalizeLanguage(languageCode);
                var resources = GetCachedLanguageResources(languageCode);

                if (resources.TryGetValue(category, out var categoryResources) &&
                    categoryResources.TryGetValue(key, out var value))
                {
                    return value;
                }

                if (category != "Common" &&
                    resources.TryGetValue("Common", out var commonResources) &&
                    commonResources.TryGetValue(key, out var commonValue))
                {
                    return commonValue;
                }

                _logger.LogWarning("未找到语言资源: Language={Language}, Category={Category}, Key={Key}",
                    languageCode, category, key);

                return $"[{category}.{key}]";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取语言字符串时发生错误: Language={Language}, Category={Category}, Key={Key}",
                    languageCode, category, key);
                return $"[{category}.{key}]";
            }
        }

        /// <summary>
        /// 查找语言资源：命中返回 true；未命中/异常返回 false，并记录服务端日志（不向界面泄露占位符）。
        /// 查找顺序与 <see cref="GetString"/> 完全一致（分类 → Common 兜底）。
        /// </summary>
        public bool TryGetString(string languageCode, string category, string key, out string value)
        {
            value = string.Empty;
            try
            {
                languageCode = ValidateAndNormalizeLanguage(languageCode);
                var resources = GetCachedLanguageResources(languageCode);

                if (resources.TryGetValue(category, out var categoryResources) &&
                    categoryResources.TryGetValue(key, out var found))
                {
                    value = found ?? string.Empty;
                    return true;
                }

                if (category != "Common" &&
                    resources.TryGetValue("Common", out var commonResources) &&
                    commonResources.TryGetValue(key, out var commonValue))
                {
                    value = commonValue ?? string.Empty;
                    return true;
                }

                _logger.LogWarning("未找到语言资源: Language={Language}, Category={Category}, Key={Key}",
                    languageCode, category, key);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取语言字符串时发生错误: Language={Language}, Category={Category}, Key={Key}",
                    languageCode, category, key);
                return false;
            }
        }

        /// <summary>
        /// 获取未做 HTML 编码的原文（见接口注释）。加载期统一做了 HtmlEncode，
        /// 此处按同一编码规则反解，供 JSON API 使用，避免操作员看到 &amp; / &#39; 字面量。
        /// </summary>
        public bool TryGetRawString(string languageCode, string category, string key, out string value)
        {
            if (TryGetString(languageCode, category, key, out var encoded))
            {
                value = WebUtility.HtmlDecode(encoded);
                return true;
            }
            value = string.Empty;
            return false;
        }

        public Dictionary<string, string> GetCategoryStrings(string languageCode, string category)
        {
            try
            {
                languageCode = ValidateAndNormalizeLanguage(languageCode);
                var resources = GetCachedLanguageResources(languageCode);

                if (resources.TryGetValue(category, out var categoryResources))
                {
                    // 返回副本，避免调用方修改缓存内部数据
                    return new Dictionary<string, string>(categoryResources, StringComparer.OrdinalIgnoreCase);
                }

                _logger.LogWarning("未找到语言分类: Language={Language}, Category={Category}",
                    languageCode, category);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分类字符串时发生错误: Language={Language}, Category={Category}",
                    languageCode, category);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public Dictionary<string, Dictionary<string, string>> GetLanguageResources(string languageCode)
        {
            languageCode = ValidateAndNormalizeLanguage(languageCode);
            // 与 GetCategoryStrings 保持一致：返回副本，避免调用方持有缓存内部实例并就地修改。
            return CreateResourcesCopy(GetCachedLanguageResources(languageCode));
        }

        public List<LanguageInfo> GetSupportedLanguages()
        {
            var cacheKey = "SupportedLanguages";

            return _cache.GetOrCreate(cacheKey, entry =>
            {
                if (_environment.IsDevelopment())
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(DevCacheDurationSeconds);
                }
                else
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheDurationMinutes);
                }

                var languages = new List<LanguageInfo>();
                var configPath = Path.Combine(_environment.ContentRootPath, LanguagesFolder, LanguageConfigFile);

                try
                {
                    if (!File.Exists(configPath))
                    {
                        _logger.LogWarning("语言配置文件不存在: {ConfigPath}", configPath);
                        return GetDefaultLanguages();
                    }

                    var doc = new XmlDocument();
                    doc.Load(configPath);

                    var languageNodes = doc.SelectNodes("/LanguageConfig/SupportedLanguages/Language");
                    if (languageNodes == null) return GetDefaultLanguages();

                    foreach (XmlNode node in languageNodes)
                    {
                        var code = node.SelectSingleNode("Code")?.InnerText;
                        var name = node.SelectSingleNode("Name")?.InnerText;
                        var nativeName = node.SelectSingleNode("NativeName")?.InnerText;
                        var isDefault = bool.TryParse(node.SelectSingleNode("IsDefault")?.InnerText, out var defaultFlag) && defaultFlag;
                        var icon = node.SelectSingleNode("Icon")?.InnerText;

                        if (!string.IsNullOrEmpty(code))
                        {
                            languages.Add(new LanguageInfo
                            {
                                Code = code,
                                Name = name ?? code,
                                NativeName = nativeName ?? name ?? code,
                                IsDefault = isDefault,
                                Icon = icon ?? string.Empty
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "读取语言配置时发生错误");
                    return GetDefaultLanguages();
                }

                if (languages.Count == 0)
                {
                    return GetDefaultLanguages();
                }

                return languages;
            })!;
        }

        public bool ValidateLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode)) return false;

            var supportedLanguages = GetSupportedLanguages();
            return supportedLanguages.Any(lang =>
                string.Equals(lang.Code, languageCode, StringComparison.OrdinalIgnoreCase));
        }

        public string GetDefaultLanguage()
        {
            var supportedLanguages = GetSupportedLanguages();
            var defaultLanguage = supportedLanguages.FirstOrDefault(lang => lang.IsDefault);
            return defaultLanguage?.Code ?? "zh-CN";
        }

        public void ClearCache()
        {
            // 先失效语言列表缓存，再按重新读取到的完整语言清单逐个清理资源缓存
            // （而非只清 zh-CN/en-US 两个硬编码键），避免新增语言后其缓存永远不会被清除。
            _cache.Remove("SupportedLanguages");
            foreach (var language in GetSupportedLanguages())
            {
                if (!string.IsNullOrEmpty(language.Code))
                    _cache.Remove($"LanguageResources_{language.Code}");
            }
            _logger.LogInformation("语言资源缓存已清除");
        }

        #region 私有方法

        /// <summary>
        /// 深拷贝资源字典，隔离缓存内部实例：
        /// 调用方（控制器 / 视图）拿到副本后即使就地修改，也不会污染缓存与其它请求。
        /// </summary>
        private static Dictionary<string, Dictionary<string, string>> CreateResourcesCopy(
            Dictionary<string, Dictionary<string, string>> resources)
        {
            var copy = new Dictionary<string, Dictionary<string, string>>(resources.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var category in resources)
            {
                copy[category.Key] = new Dictionary<string, string>(category.Value, StringComparer.OrdinalIgnoreCase);
            }
            return copy;
        }

        private Dictionary<string, Dictionary<string, string>> GetCachedLanguageResources(string languageCode)
        {
            var cacheKey = $"LanguageResources_{languageCode}";

            return _cache.GetOrCreate(cacheKey, entry =>
            {
                if (_environment.IsDevelopment())
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(DevCacheDurationSeconds);
                }
                else
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheDurationMinutes);
                }
                return LoadLanguageResourcesFromXml(languageCode);
            })!;
        }

        private Dictionary<string, Dictionary<string, string>> LoadLanguageResourcesFromXml(string languageCode)
        {
            var resources = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var languageFile = $"{languageCode}.xml";
            var languagePath = Path.Combine(_environment.ContentRootPath, LanguagesFolder, languageFile);

            try
            {
                if (!File.Exists(languagePath))
                {
                    _logger.LogWarning("语言文件不存在，使用默认语言: {LanguagePath}", languagePath);
                    var defaultLanguage = GetDefaultLanguage();
                    if (languageCode != defaultLanguage)
                    {
                        return LoadLanguageResourcesFromXml(defaultLanguage);
                    }
                    return CreateDefaultResources();
                }

                var doc = new XmlDocument();
                doc.Load(languagePath);

                // 修改这里：使用正确的 XPath 路径
                var languageNode = doc.SelectSingleNode("/LanguageResources/Language");
                if (languageNode == null)
                {
                    _logger.LogError("XML 文件格式错误，找不到 Language 节点: {LanguagePath}", languagePath);
                    return CreateDefaultResources();
                }

                var resourcesNode = languageNode.SelectSingleNode("Resources");
                if (resourcesNode == null)
                {
                    _logger.LogError("XML 文件格式错误，找不到 Resources 节点: {LanguagePath}", languagePath);
                    return CreateDefaultResources();
                }

                // 遍历 Resources 下的所有子节点（Common, Login, Home 等）
                foreach (XmlNode categoryNode in resourcesNode.ChildNodes)
                {
                    if (categoryNode.NodeType != XmlNodeType.Element)
                        continue;

                    var categoryName = categoryNode.Name;
                    var categoryResources = new Dictionary<string, string>();

                    foreach (XmlNode resourceNode in categoryNode.ChildNodes)
                    {
                        if (resourceNode.NodeType == XmlNodeType.Element)
                        {
                            // P0-3 存储型 XSS 修复：语言值可由 Settings 权限用户通过 Language/Edit 写入，
                            // 读取后会经 @Html.Raw 渲染到页面。在加载时统一做 HTML 编码，
                            // 让 GetString / GetCategoryStrings / GetLanguageResources / 预加载 GetLocalText
                            // 所有出口拿到的都是已编码文本，避免脚本注入。
                            // 副作用：含 & 的文案在 JS 字符串上下文中会显示为 &amp;（纯文本告警），
                            // 但换来了横向 XSS 的阻断，可接受。
                            categoryResources[resourceNode.Name] =
                                WebUtility.HtmlEncode(resourceNode.InnerText ?? string.Empty);
                        }
                    }

                    resources[categoryName] = categoryResources;
                }

                _logger.LogDebug("已加载语言资源: {Language}, 分类数: {CategoryCount}",
                    languageCode, resources.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载语言资源时发生错误: {LanguagePath}", languagePath);
                return CreateDefaultResources();
            }

            return resources;
        }

        private Dictionary<string, Dictionary<string, string>> CreateDefaultResources()
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                ["Common"] = new Dictionary<string, string>
                {
                    ["AppName"] = "GCM Warehouse System",
                    ["Loading"] = "Loading...",
                    ["Success"] = "Success",
                    ["Error"] = "Error"
                },
                ["Login"] = new Dictionary<string, string>
                {
                    ["Title"] = "Login",
                    ["Username"] = "Username",
                    ["Password"] = "Password",
                    ["LoginButton"] = "Login"
                },
                ["Home"] = new Dictionary<string, string>
                {
                    ["Title"] = "Home",
                    ["Welcome"] = "Welcome",
                    ["LogoutButton"] = "Logout"
                }
            };
        }

        private string ValidateAndNormalizeLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return GetDefaultLanguage();

            languageCode = languageCode.Trim();

            if (languageCode.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                languageCode.Equals("zh-cn", StringComparison.OrdinalIgnoreCase))
                return "zh-CN";

            if (languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                languageCode.Equals("en-us", StringComparison.OrdinalIgnoreCase))
                return "en-US";

            if (ValidateLanguage(languageCode))
                return languageCode;

            return GetDefaultLanguage();
        }

        private List<LanguageInfo> GetDefaultLanguages()
        {
            return new List<LanguageInfo>
            {
                new LanguageInfo
                {
                    Code = "zh-CN",
                    Name = "简体中文",
                    NativeName = "简体中文",
                    IsDefault = true,
                    Icon = "🇨🇳"
                },
                new LanguageInfo
                {
                    Code = "en-US",
                    Name = "English",
                    NativeName = "English",
                    IsDefault = false,
                    Icon = "🇺🇸"
                }
            };
        }

        #endregion
    }

    public class LanguageInfo
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string NativeName { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public string Icon { get; set; } = string.Empty;
    }


}