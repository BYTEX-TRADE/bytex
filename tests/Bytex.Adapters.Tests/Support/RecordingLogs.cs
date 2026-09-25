using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Tests.Support;

/// <summary>
/// Keeps what a client logged, so a test can assert that something was NOT reported as a failure.
/// <para>
/// Needed because "it did not break" is too weak a claim for some behaviour. A venue refusing to set a leverage
/// already in force is not a failure - the account is exactly where it was asked to be - and a client that let that
/// reach its general error handler would start the node just the same while putting an error in front of somebody
/// for nothing. Only the log tells those two apart.
/// </para>
/// </summary>
internal sealed class RecordingLogs : ILoggerFactory
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)];
            }
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];
            }
        }
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this);

    public void Dispose()
    {
    }

    private void Add(LogLevel level, string message)
    {
        lock (_gate)
        {
            _entries.Add((level, message));
        }
    }

    private sealed class Sink(RecordingLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Add(logLevel, formatter(state, exception));
        }
    }
}
