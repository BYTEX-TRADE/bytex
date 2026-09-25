namespace Bytex.Core.Trading;

/// <summary>
/// A strategy that can describe its inner state to a monitor (the node control channel, a dashboard). The engine asks on
/// the kernel thread and serializes the answer as JSON, so the object should be a plain, immutable snapshot.
/// </summary>
public interface IStrategyMonitorView
{
    /// <summary>The snapshot, or null when there is nothing to show yet.</summary>
    object? MonitorView();
}
