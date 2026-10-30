using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;
using ZXing;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>配置管理</summary>
    public partial class BarcodeController
    {
        #region 配置管理

        /// <summary>用户配置写入序列化选项（静态缓存，避免每次写入重复实例化，P3-consistency）</summary>
        private static readonly JsonSerializerSettings ConfigWriteOptions = new() { Formatting = Formatting.Indented };

        /// <summary>用户配置文件读-改-写锁：同一文件的并发保存必须串行，避免后写覆盖先写</summary>
        private static readonly SemaphoreSlim ConfigFileLock = new(1, 1);

        /// <summary>
        /// 只保存配置字段：裁剪掉 MaterialCode/Quantity 等业务字段，避免持久化文件污染
        /// （PrintCount 也保留，是用户偏好的一部分）
        /// </summary>
        private static readonly Func<BarcodePrintModel, BarcodePrintModel> ConfigProjection = src =>
            new BarcodePrintModel
            {
                LabelWidth     = src.LabelWidth,
                LabelHeight    = src.LabelHeight,
                PaddingLeft    = src.PaddingLeft,
                PaddingRight   = src.PaddingRight,
                PaddingTop     = src.PaddingTop,
                PaddingBottom  = src.PaddingBottom,
                FontFamily     = src.FontFamily,
                FontSize       = src.FontSize,
                FontWeight     = src.FontWeight,
                BarcodeWidth   = src.BarcodeWidth,
                BarcodeHeight  = src.BarcodeHeight,
                BarcodeOffsetX = src.BarcodeOffsetX,
                BarcodeOffsetY = src.BarcodeOffsetY,
                PrintCount     = src.PrintCount,
                IsLandscape    = src.IsLandscape,
                PrintName      = src.PrintName
            };

        [HttpGet]
        public async Task<IActionResult> LoadConfig()
        {
            string userName = GetCurrentUserName();
            if (string.IsNullOrEmpty(userName))
                return Json(null);

            var config = await ReadUserConfigAsync(userName);
            return Json(config);
        }

        /// <summary>
        /// 读取当前用户配置（MemoryCache 优先，缓存 10 分钟，减少 JSON 文件 IO）
        /// 键带租户（公司|工厂）：跨公司同名用户的个人配置不互相命中
        /// </summary>
        private async Task<BarcodePrintModel?> ReadUserConfigAsync(string userName)
        {
            string userKey = $"{CurrentTenantKey}|{userName}";
            string cacheKey = CachePrefix_ConfigUser + userKey;
            if (_memoryCache.TryGetValue(cacheKey, out BarcodePrintModel? cached))
                return cached;

            if (!System.IO.File.Exists(_configPath))
                return null;

            try
            {
                var json = await System.IO.File.ReadAllTextAsync(_configPath);
                var userConfigs = JsonConvert.DeserializeObject<Dictionary<string, BarcodePrintModel>>(json);
                BarcodePrintModel? config = null;
                if (userConfigs != null && userConfigs.TryGetValue(userKey, out var c))
                    config = ConfigProjection(c);

                _memoryCache.Set(cacheKey, config, TimeSpan.FromSeconds(CacheTtl_ConfigSeconds));
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载用户配置失败 User={User}", userName);
                return null;
            }
        }

        /// <summary>
        /// 保存当前用户配置：统一入口（替换原来的 SaveOnlySpecifiedConfig + LoadConfigToModel 各自文件读取的重复 IO）
        /// 写文件后使配置缓存失效，下一次读自然是最新数据
        /// </summary>
        private async Task SaveUserConfigAsync(BarcodePrintModel model)
        {
            string userName = GetCurrentUserName();
            if (string.IsNullOrEmpty(userName)) return;

            // 读-改-写整体串行化：并发保存时，后进入者会读到前一次写入的结果，不会互相覆盖
            await ConfigFileLock.WaitAsync();
            try
            {
                Dictionary<string, BarcodePrintModel> userConfigs;
                if (System.IO.File.Exists(_configPath))
                {
                    try
                    {
                        var json = await System.IO.File.ReadAllTextAsync(_configPath);
                        userConfigs = JsonConvert.DeserializeObject<Dictionary<string, BarcodePrintModel>>(json)
                                      ?? new Dictionary<string, BarcodePrintModel>();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "读取配置文件失败，将新建配置文件");
                        userConfigs = new Dictionary<string, BarcodePrintModel>();
                    }
                }
                else
                {
                    userConfigs = new Dictionary<string, BarcodePrintModel>();
                }

                // 只保存配置字段（去掉 MaterialCode/Qty 等临时业务数据），防止文件无限膨胀/串数据
                // 键带租户（公司|工厂）：跨公司同名用户的个人配置不互相覆盖
                string userKey = $"{CurrentTenantKey}|{userName}";
                userConfigs[userKey] = ConfigProjection(model);

                // 原子写：先写唯一临时文件再替换，避免进程崩溃/并发读到半截 JSON
                await AtomicFileWriter.WriteAtomicAsync(_configPath, JsonConvert.SerializeObject(userConfigs, ConfigWriteOptions));

                // 写入后主动失效缓存（而非立即写入：避免其他并发覆盖）
                _memoryCache.Remove(CachePrefix_ConfigUser + userKey);
            }
            finally
            {
                ConfigFileLock.Release();
            }
        }

        /// <summary>
        /// Index 页面首次渲染时，把已持久化的配置回填到 Model
        /// （实现方式：读取配置后把属性赋给传入的 model，保持 Razor 层契约不变）
        /// </summary>
        private async Task LoadConfigToModel(BarcodePrintModel model)
        {
            string userName = GetCurrentUserName();
            if (string.IsNullOrEmpty(userName)) return;

            var config = await ReadUserConfigAsync(userName);
            if (config == null) return;

            // 将持久化配置回填到页面模型（只拷贝配置字段，不覆盖业务字段默认值）
            model.LabelWidth     = config.LabelWidth;
            model.LabelHeight    = config.LabelHeight;
            model.PaddingLeft    = config.PaddingLeft;
            model.PaddingRight   = config.PaddingRight;
            model.PaddingTop     = config.PaddingTop;
            model.PaddingBottom  = config.PaddingBottom;
            model.FontFamily     = config.FontFamily;
            model.FontSize       = config.FontSize;
            model.FontWeight     = config.FontWeight;
            model.BarcodeWidth   = config.BarcodeWidth;
            model.BarcodeHeight  = config.BarcodeHeight;
            model.BarcodeOffsetX = config.BarcodeOffsetX;
            model.BarcodeOffsetY = config.BarcodeOffsetY;
            model.PrintCount     = config.PrintCount;
            model.IsLandscape    = config.IsLandscape;
            model.PrintName      = config.PrintName;
        }

        #endregion
    }
}