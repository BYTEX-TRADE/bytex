using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Live.Persistence;

/// <summary>
/// Writes every log line the node produces into its journal as well as to wherever it already went, so the reason a node
/// did something is still there after the process is gone.
/// </summary>
internal sealed class JournalLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly NodeStore _store;
    private readonly Func<UnixNanos> _now;

    public JournalLoggerFactory(ILoggerFactory inner, NodeStore store, Func<UnixNanos> now)
    {
        _inner = inner;
        _store = store;
        _now = now;
    }

    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    public ILogger CreateLogger(string categoryName) => new JournalLogger(_inner.CreateLogger(categoryName), _store, categoryName, _now);

    public void Dispose() => _inner.Dispose();

    private sealed class JournalLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly NodeStore _store;
        private readonly string _category;
        private readonly Func<UnixNanos> _now;

        public JournalLogger(ILogger inner, NodeStore store, string category, Func<UnixNanos> now)
        {
            _inner = inner;
            _store = store;
            _category = category;
            _now = now;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        // The journal takes Information and worse whatever the inner logger is set to: the evidence must not depend on
        // how the host happens to be configured today.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information || _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (_inner.IsEnabled(logLevel))
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
            }

            if (logLevel < LogLevel.Information || logLevel == LogLevel.None)
            {
                return;
            }

            string message = formatter(state, exception);
            if (exception is not null)
            {
                message = message + " | " + exception.GetType().Name + ": " + exception.Message;
            }

            _store.Append(new JournalRecord(_now(), "log", Level: logLevel.ToString(), Source: _category, Message: message));
        }
    }
}
