using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// 每日单据操作明细日志服务（Singleton）
    /// 职责：将每次提交的单据（发料 / 退料 / 工单入库 / 库存转移 / 销售出货 / 采购收货 / 库存调整 / 盘点标签）
    /// 按行追加到按天滚动的明细文本文件：logs/单据操作明细_yyyyMMdd.txt（UTF-8 带 BOM，可直接用 Excel 打开）。
    /// 行格式：模块名称,提交的单据,操作日期,操作人,提交状态（成功/失败）
    /// 说明：提交状态对每次真实提交到 Epicor 的尝试均记录——成功记"成功"，Epicor 拒绝或异常记"失败"；
    ///       提交前的参数校验失败不记明细（单据尚未提交）。
    /// 设计约束：明细写入失败只记诊断日志，绝不允许影响业务主流程。
    /// </summary>
    public class DocumentOperationLogService
    {
        private readonly string _logDirectory;
        private readonly ILogger<DocumentOperationLogService>? _logger;
        private readonly object _writeLock = new();
        private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

        private const string FilePrefix = "单据操作明细_";
        private const string HeaderLine = "模块名称,提交的单据,操作日期,操作人,提交状态";

        public DocumentOperationLogService(IHostEnvironment env, ILogger<DocumentOperationLogService>? logger = null)
        {
            _logDirectory = Path.Combine(env.ContentRootPath, "logs");
            _logger = logger;
        }

        /// <summary>
        /// 记录一条单据提交明细（成功与失败均记录）。
        /// </summary>
        /// <param name="moduleName">模块名称（如：发料管理 / 销售出货）</param>
        /// <param name="document">提交的单据（单据号 + 关键明细，禁止包含英文逗号）</param>
        /// <param name="operatorId">操作人</param>
        /// <param name="success">提交状态：true=成功，false=失败</param>
        public void Log(string moduleName, string document, string? operatorId, bool success)
        {
            try
            {
                string line = $"{Clean(moduleName)},{Clean(document)},{DateTime.Now:yyyy-MM-dd HH:mm:ss},{Clean(operatorId)},{(success ? "成功" : "失败")}";

                lock (_writeLock)
                {
                    Directory.CreateDirectory(_logDirectory);
                    string filePath = Path.Combine(_logDirectory, $"{FilePrefix}{DateTime.Now:yyyyMMdd}.txt");

                    // 新文件先写表头，保证明细文件自描述、Excel 可直接识别列
                    if (!File.Exists(filePath))
                    {
                        File.AppendAllText(filePath, HeaderLine + Environment.NewLine, Utf8WithBom);
                    }
                    File.AppendAllText(filePath, line + Environment.NewLine, Utf8WithBom);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "单据操作明细写入失败（不影响业务主流程）：模块 {Module}，单据 {Document}，操作人 {Operator}",
                    moduleName, document, operatorId);
            }
        }

        /// <summary>清理字段内的英文逗号与换行符，避免破坏"按行 + 按英文逗号分隔"的明细格式。</summary>
        private static string Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Replace(',', '，').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
