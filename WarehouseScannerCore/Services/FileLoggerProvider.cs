using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;

namespace WarehouseScannerCore.Services
{
    public class FileLoggerProvider : ILoggerProvider
    {
        private readonly string _logDirectory;
        private readonly string _fileNamePrefix;
        private readonly long _maxFileSizeBytes;
        private readonly int _maxRetainedFiles;
        private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
        private readonly object _lock = new();
        private StreamWriter? _currentWriter;
        private string _currentFilePath = null!;
        private long _currentFileSize;
        private int _pendingFlushCount;
        private const int FlushInterval = 5; // 每 5 条日志 Flush 一次，减少 I/O

        public FileLoggerProvider(
            string logDirectory,
            string fileNamePrefix = "log",
            long maxFileSizeBytes = 10 * 1024 * 1024,
            int maxRetainedFiles = 30)
        {
            _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
            _fileNamePrefix = fileNamePrefix;
            _maxFileSizeBytes = maxFileSizeBytes;
            _maxRetainedFiles = maxRetainedFiles;

            Directory.CreateDirectory(_logDirectory);
            CleanupOldFiles(); // 启动时清理超量旧日志
            OpenNewLogFile();
            CleanupOldFiles(); // 新文件可能使总数超过上限，再收敛一次（保留含新文件在内的最新 N 个）
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    _currentWriter?.Flush(); // 切换文件前确保缓冲已写入
                    _currentWriter?.Dispose();
                    _currentWriter = null;
                }
            }
        }

        internal void WriteMessage(string message, LogLevel level = LogLevel.Information)
        {
            lock (_lock)
            {
                if (_currentWriter == null) return;

                // 仅在文件超过体积上限时滚动；文件个数超限由 OpenNewLogFile 后的 CleanupOldFiles 处理。
                // 若在每条日志都 Directory.GetFiles 枚举目录，高并发下会造成严重的 I/O 放大。
                if (_currentFileSize > _maxFileSizeBytes)
                {
                    OpenNewLogFile();
                    CleanupOldFiles();
                }

                _currentWriter.WriteLine(message);
                _currentFileSize += message.Length + Environment.NewLine.Length;

                // Info 及以上级别立即 Flush（业务操作日志不能丢），Trace/Debug 批量 Flush 减少 I/O
                if (level >= LogLevel.Information || ++_pendingFlushCount >= FlushInterval)
                {
                    _currentWriter.Flush();
                    _pendingFlushCount = 0;
                }
            }
        }

        private void OpenNewLogFile()
        {
            _currentWriter?.Dispose();
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            // 同一秒内多次滚动会生成同名文件；配合 FileMode.Append 会写回同一文件使滚动失效。
            // 保留原文件名前缀与时间戳格式（不影响既有日志检索），仅在冲突时追加序号。
            string basePath = Path.Combine(_logDirectory, $"{_fileNamePrefix}_{timestamp}");
            string path = basePath + ".log";
            for (int suffix = 1; File.Exists(path); suffix++)
                path = $"{basePath}_{suffix:D3}.log";

            _currentFilePath = path;
            _currentWriter = new StreamWriter(new FileStream(_currentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
            _currentFileSize = new FileInfo(_currentFilePath).Length;
        }

        private void CleanupOldFiles()
        {
            try
            {
                var files = Directory.GetFiles(_logDirectory, $"{_fileNamePrefix}_*.log")
                    .OrderByDescending(f => f)
                    .Skip(_maxRetainedFiles)
                    .ToList();

                foreach (var file in files)
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"删除旧日志文件失败: {file}: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理旧日志文件失败: {ex.Message}");
            }
        }

        private class FileLogger : ILogger
        {
            private readonly FileLoggerProvider _provider;
            private readonly string _categoryName;

            public FileLogger(FileLoggerProvider provider, string categoryName)
            {
                _provider = provider;
                _categoryName = categoryName;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (formatter == null) return;
                string message = formatter(state, exception);
                if (string.IsNullOrEmpty(message) && exception == null) return;

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string level = logLevel.ToString().ToUpperInvariant();
                string logLine = $"[{timestamp}] [{level}] [{_categoryName}] {message}";
                if (exception != null)
                    logLine += Environment.NewLine + exception;

                _provider.WriteMessage(logLine, logLevel);
            }
        }
    }
}
