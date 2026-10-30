﻿﻿﻿﻿﻿﻿﻿using System;
using System.IO;
using System.Threading.Tasks;

namespace WarehouseScannerCore.Utilities
{
    /// <summary>
    /// 配置文件原子写入工具（统一 ConfigService / UserMenuPermissionService 的落盘方式）。
    ///
    /// 写入流程：先写同目录临时文件（*.tmp），再 File.Replace / File.Move 原子替换。
    /// 进程在写入中途崩溃/断电时最多留下 .tmp 残留文件，不会把原配置文件写成半截 JSON。
    ///
    /// 与 FactoryConfigService.IO.cs 中已验证的 WriteAtomic / ReplaceFile 为同一范式，
    /// 此处提升为公共工具方法供各配置服务复用。
    /// </summary>
    public static class AtomicFileWriter
    {
        /// <summary>同步原子写入</summary>
        public static void WriteAtomic(string path, string content)
        {
            // 临时文件名带 GUID：同一路径的并发写入各自使用独立临时文件，避免互相覆盖/读到对方半截内容
            var tmpPath = CreateTempPath(path);
            try
            {
                File.WriteAllText(tmpPath, content);
                ReplaceFile(tmpPath, path);
            }
            finally
            {
                CleanupTempFile(tmpPath);
            }
        }

        /// <summary>异步原子写入</summary>
        public static async Task WriteAtomicAsync(string path, string content)
        {
            var tmpPath = CreateTempPath(path);
            try
            {
                await File.WriteAllTextAsync(tmpPath, content);
                ReplaceFile(tmpPath, path);
            }
            finally
            {
                CleanupTempFile(tmpPath);
            }
        }

        /// <summary>生成同目录下的唯一临时文件路径</summary>
        private static string CreateTempPath(string path)
            => path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        /// <summary>清理替换成功后不应存在的临时文件（以及写失败时的残留）</summary>
        private static void CleanupTempFile(string tmpPath)
        {
            try
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
                // 清理失败不影响主流程：残留的 .tmp 文件不会覆盖正式配置文件
            }
        }

        /// <summary>
        /// 原子替换：目标存在用 File.Replace（保留原文件句柄语义，替换为原子操作）；
        /// 不存在（首次创建）用 File.Move。
        /// </summary>
        private static void ReplaceFile(string tmpPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
                File.Replace(tmpPath, destinationPath, null);
            else
                File.Move(tmpPath, destinationPath);
        }
    }
}
