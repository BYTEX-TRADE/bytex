using Microsoft.Extensions.Logging;

namespace Bytex.Core.Tests.Support;

/// <summary>
/// Records what was logged and under which category, so a test can hold a component to logging as itself: the
/// category is how a log pipeline attributes a line, and the named values are what a structured sink stores.
/// </summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    public List<(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values)> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, Lines);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Recorder : ILogger
    {
        private readonly string _category;
        private readonly List<(string, LogLevel, string, IReadOnlyList<KeyValuePair<string, object?>>)> _lines;

        public Recorder(string category, List<(string, LogLevel, string, IReadOnlyList<KeyValuePair<string, object?>>)> lines)
        {
            _category = category;
            _lines = lines;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            IReadOnlyList<KeyValuePair<string, object?>> values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            lock (_lines)
            {
                _lines.Add((_category, logLevel, formatter(state, exception), values));
            }
        }
    }
}
