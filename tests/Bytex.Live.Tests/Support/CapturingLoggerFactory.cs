using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Bytex.Live.Tests.Support;

/// <summary>
/// Collects formatted log lines so tests can wait for one without polling.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Logger(_lines.Writer);

    /// <summary>Waits for the next line containing the fragment; fails the test after the timeout.</summary>
    public async Task<string> WaitForAsync(string fragment, TimeSpan? timeout = null)
    {
        using CancellationTokenSource cts = new(timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            string line = await _lines.Reader.ReadAsync(cts.Token);
            if (line.Contains(fragment, StringComparison.Ordinal))
            {
                return line;
            }
        }
    }

    public void Dispose() => _lines.Writer.TryComplete();

    private sealed class Logger : ILogger
    {
        private readonly ChannelWriter<string> _writer;

        public Logger(ChannelWriter<string> writer) => _writer = writer;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _writer.TryWrite($"{logLevel}: {formatter(state, exception)}");
    }
}
