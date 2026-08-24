using System.Threading.Channels;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Live;

/// <summary>
/// Runs the kernel on a dedicated thread fed by a bounded queue. Everything that enters from I/O or timers is posted here.
/// </summary>
public sealed class LiveKernelLoop : IDisposable
{
    private readonly Channel<Action> _queue;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;

    public LiveKernelLoop(int capacity = 100_000, ILoggerFactory? loggerFactory = null, string threadName = "bytex-kernel")
    {
        _queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        _log = loggerFactory?.CreateLogger<LiveKernelLoop>() ?? NullLogger<LiveKernelLoop>.Instance;
        _thread = new Thread(Run) { Name = threadName, IsBackground = true };
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    public bool IsOnLoopThread => Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId;

    public long Processed { get; private set; }

    public int Pending => _queue.Reader.Count;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _thread.Start();
    }

    /// <summary>
    /// Queues an action for the kernel thread, blocking the caller when the queue is full (back-pressure).
    /// </summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsOnLoopThread)
        {
            action();
            return;
        }

        if (!_queue.Writer.TryWrite(action))
        {
            _queue.Writer.WriteAsync(action).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Queues an action and returns a task that completes when it has run.
    /// </summary>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsOnLoopThread)
        {
            action();
            return Task.CompletedTask;
        }

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
        return tcs.Task;
    }

    private void Run()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Action action;
                try
                {
                    if (!_queue.Reader.TryRead(out action!))
                    {
                        if (!_queue.Reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult())
                        {
                            break;
                        }

                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Unhandled exception on the kernel thread");
                }

                Processed++;
            }
        }
        finally
        {
            _stopped.TrySetResult();
        }
    }

    /// <summary>
    /// Drains remaining work and stops the thread.
    /// </summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (!_started)
        {
            return;
        }

        _queue.Writer.TryComplete();
        Task wait = _stopped.Task;
        if (await Task.WhenAny(wait, Task.Delay(timeout ?? TimeSpan.FromSeconds(10))).ConfigureAwait(false) != wait)
        {
            _log.LogWarning("Kernel loop did not drain in time; cancelling");
            _cts.Cancel();
            await wait.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _cts.Dispose();
    }
}

/// <summary>
/// Marshals data client callbacks onto the kernel thread.
/// </summary>
public sealed class DispatchingDataSink : IDataClientSink
{
    private readonly IDataClientSink _inner;
    private readonly LiveKernelLoop _loop;

    public DispatchingDataSink(IDataClientSink inner, LiveKernelLoop loop)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
    }

    public void OnData(IData data) => _loop.Post(() => _inner.OnData(data));

    public void OnInstrument(Instrument instrument) => _loop.Post(() => _inner.OnInstrument(instrument));

    public void OnResponse(DataResponse response) => _loop.Post(() => _inner.OnResponse(response));

    public void OnConnected(ClientId clientId) => _loop.Post(() => _inner.OnConnected(clientId));

    public void OnDisconnected(ClientId clientId, string reason) => _loop.Post(() => _inner.OnDisconnected(clientId, reason));

    public void OnSubscriptionFailed(ClientId clientId, SubscribeCommand command, string reason) => _loop.Post(() => _inner.OnSubscriptionFailed(clientId, command, reason));
}

/// <summary>
/// Marshals execution client callbacks onto the kernel thread.
/// </summary>
public sealed class DispatchingExecutionSink : IExecutionClientSink
{
    private readonly IExecutionClientSink _inner;
    private readonly LiveKernelLoop _loop;

    public DispatchingExecutionSink(IExecutionClientSink inner, LiveKernelLoop loop)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
    }

    public void OnOrderEvent(OrderEvent e) => _loop.Post(() => _inner.OnOrderEvent(e));

    public void OnAccountState(AccountState state) => _loop.Post(() => _inner.OnAccountState(state));

    public void OnConnected(ClientId clientId) => _loop.Post(() => _inner.OnConnected(clientId));

    public void OnDisconnected(ClientId clientId, string reason) => _loop.Post(() => _inner.OnDisconnected(clientId, reason));
}
