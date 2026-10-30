using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarehouseScanner.Services;
using WarehouseScannerCore.Filters;
using WarehouseScannerCore.Models;
using WarehouseScannerCore.Models.DTOs;
using WarehouseScannerCore.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Controllers
{
    /// <summary>用户 PackID 管理</summary>
    public partial class OutboundController
    {
        #region 用户PackID管理
        //
        // 共享 JSON 文件 App_Data/UserPackId.json，格式：{ "644": "2165", "488": "2166" }
        // 全局锁保证 read-modify-write 原子性，支持多用户并发写入各自的 key。
        //
        private static readonly SemaphoreSlim _packFileLock = new(1, 1);

        private string PackIdFilePath => Path.Combine(_environment.ContentRootPath, "App_Data", "UserPackId.json");

        /// <summary>PackID 文件键：租户（公司|工厂）+ 用户，跨公司同名用户不互相覆盖"上次 PackID"</summary>
        private string PackIdFileKey(string userId)
            => $"{CurrentTenantKey}|{userId}";

        private async Task<string> ReadUserLastPackIdAsync(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId)) return string.Empty;

                await _packFileLock.WaitAsync();
                try
                {
                    string file = PackIdFilePath;
                    if (!System.IO.File.Exists(file)) return string.Empty;

                    string json = await System.IO.File.ReadAllTextAsync(file);
                    var obj = JObject.Parse(json);
                    return obj[PackIdFileKey(userId)]?.Value<string>() ?? string.Empty;
                }
                finally
                {
                    _packFileLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取用户PackID失败 UserId:{UserId}", userId);
                return string.Empty;
            }
        }

        private async Task SaveUserLastPackIdAsync(string userId, string packId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(packId))
                {
                    _logger.LogWarning("用户 PackID 跳过保存：UserId 或 PackId 为空，UserId:{UserId}, PackId:{PackId}", userId, packId);
                    return;
                }

                string file = PackIdFilePath;
                string dir = Path.GetDirectoryName(file)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                await _packFileLock.WaitAsync();
                try
                {
                    // read-modify-write：读现有 JSON → 更新当前用户 key → 写回
                    JObject obj;
                    if (System.IO.File.Exists(file))
                    {
                        string json = await System.IO.File.ReadAllTextAsync(file);
                        obj = string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
                    }
                    else
                    {
                        obj = new JObject();
                    }
                    obj[PackIdFileKey(userId)] = packId;
                    await System.IO.File.WriteAllTextAsync(file, obj.ToString(Newtonsoft.Json.Formatting.Indented));
                    _logger.LogInformation("用户 {UserId} 保存最新PackID: {PackId}", userId, packId);
                }
                finally
                {
                    _packFileLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存用户PackID失败 UserId:{UserId},PackId:{PackId}", userId, packId);
            }
        }
        #endregion
    }
}