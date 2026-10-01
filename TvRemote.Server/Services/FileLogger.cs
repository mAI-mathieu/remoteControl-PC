namespace TvRemote.Services;
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly string logDirectory = directory;
    private readonly object gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && !category.StartsWith("Microsoft.AspNetCore");
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            lock (provider.gate)
            {
                try
                {
                    var path = Path.Combine(provider.logDirectory, "host.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
                    File.AppendAllText(path, $"{DateTimeOffset.Now:O} {level} {category}: {formatter(state, exception)}{Environment.NewLine}");
                }
                catch (IOException) { }
            }
        }
    }
}
