using System.Text;

namespace ImageWriterII.Service;

/// <summary>Tiny daily-rolling file logger so the service leaves a trail without external packages.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateTime _writerDate;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        CleanOld();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_sync)
        {
            var today = DateTime.Now.Date;
            if (_writer is null || _writerDate != today)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(Path.Combine(_directory, $"imagewriter-{today:yyyyMMdd}.log"), append: true, Encoding.UTF8) { AutoFlush = true };
                bool rolled = _writerDate != default;
                _writerDate = today;
                // Prune on every roll, not only at startup: this service is meant to run for months.
                if (rolled) CleanOld();
            }
            // Job and user names come from the network; a newline in one would forge log lines.
            _writer.WriteLine(line.ReplaceLineEndings(" "));
        }
    }

    private void CleanOld()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_directory, "imagewriter-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch (Exception) { /* ignore */ }
    }

    public void Dispose()
    {
        lock (_sync) { _writer?.Dispose(); _writer = null; }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _category;
        public FileLogger(FileLoggerProvider p, string category) { _p = p; _category = category; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string level = logLevel switch
            {
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "DBG"
            };
            string cat = _category.Length > 40 ? _category[(_category.LastIndexOf('.') + 1)..] : _category;
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ").Append(cat).Append(": ").Append(formatter(state, exception));
            if (exception is not null) sb.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            _p.Write(sb.ToString());
        }
    }
}
