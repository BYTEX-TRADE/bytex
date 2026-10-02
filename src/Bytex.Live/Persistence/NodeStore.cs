using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Bytex.Live.Persistence;

public sealed record NodeStoreConfig
{
    /// <summary>Where the node writes; the journal and the saved state live in subfolders of it.</summary>
    public required string Directory { get; init; }

    /// <summary>Journal files older than this are deleted when the node starts; zero keeps them all.</summary>
    public int JournalDays { get; init; } = 30;

    /// <summary>False writes the journal but never restores a strategy's own state at start.</summary>
    public bool RestoreState { get; init; } = true;

    /// <summary>
    /// How often a running node writes what its strategies know, over and above the write it does when it is asked
    /// to stop. Zero saves only on a stop.
    ///
    /// <para>
    /// It defaults to something rather than nothing because a stop is the case that was already covered. What was
    /// not is every other way a node ends: a container stopping, a service manager killing it, a crash, a power cut -
    /// and a host that kills the process after its own shutdown timeout, which is ordinary use rather than a
    /// disaster. In all of those the strategy came back having forgotten which phase it was in, holding a position it
    /// re-adopted from the venue. That is a different strategy holding somebody's money, and nothing about it looks
    /// wrong.
    /// </para>
    ///
    /// <para>
    /// Thirty seconds is a trade between what a crash can lose and what the disk is asked to do. The write itself is
    /// small - one JSON file per runtimeModule - and atomic, so a crash during one leaves the previous save intact rather
    /// than a half-written file.
    /// </para>
    /// </summary>
    public TimeSpan SaveInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>What the journal holds: one line of a node's history.</summary>
public sealed record JournalRecord(
    UnixNanos Ts,
    string Kind,
    string? Topic = null,
    string? Level = null,
    string? Source = null,
    string? Message = null,
    JsonElement? Payload = null);

/// <summary>
/// A live node's evidence on disk: an append-only journal of everything it logged, every order, position and account
/// event it saw, and the state its strategies were in when it stopped. A node that is stopped and started again reads
/// both back, so what it did survives the process and a resumed strategy goes on where it left off.
/// </summary>
public sealed class NodeStore : IAsyncDisposable
{
    /// <summary>
    /// How many lines may wait to be written. A node must not stall because the disk is slow, so the oldest lines are
    /// dropped once this many are queued, and the drop is counted.
    /// </summary>
    private const int QueueCapacity = 100_000;

    /// <summary>How long a flush waits for the writer to catch up before it gives up on the tail.</summary>
    public static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How often a flush looks again while it waits.</summary>
    private static readonly TimeSpan FlushPoll = TimeSpan.FromMilliseconds(5);

    private static readonly JsonSerializerOptions Lines = new(BytexJson.Options) { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly NodeStoreConfig _config;
    private readonly ILogger _log;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), QueueCapacity);
    private readonly Task _writer;
    private long _dropped;
    private long _enqueued;
    private long _written;

    public NodeStore(NodeStoreConfig config, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _log = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger<NodeStore>();
        StateDirectory = Path.Combine(config.Directory, "state");
        JournalDirectory = Path.Combine(config.Directory, "journal");
        System.IO.Directory.CreateDirectory(StateDirectory);
        System.IO.Directory.CreateDirectory(JournalDirectory);
        Prune();
        _writer = Task.Run(WriteLoop);
    }

    public string StateDirectory { get; }

    public string JournalDirectory { get; }

    /// <summary>Records the journal could not take because the writer fell behind; it should stay at zero.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    // ----- Journal -----

    public void Append(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            // The tradingRuntime thread must never wait on a disk write, so a journal that cannot keep up loses lines and says so.
            if (_queue.TryAdd(JsonSerializer.Serialize(record, Lines)))
            {
                Interlocked.Increment(ref _enqueued);
            }
            else
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or NotSupportedException)
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private void WriteLoop()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            try
            {
                string path = Path.Combine(JournalDirectory, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
                long count = 1;
                using (StreamWriter writer = new(path, append: true, Encoding.UTF8))
                {
                    writer.WriteLine(line);
                    while (_queue.TryTake(out string? next))
                    {
                        writer.WriteLine(next);
                        count++;
                    }
                }

                // Counted only once the file is closed, so a flush that returns means the lines are on disk.
                Interlocked.Add(ref _written, count);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _dropped);
                _log.LogWarning(e, "Journal line could not be written to {Directory}", JournalDirectory);
            }
        }
    }

    /// <summary>
    /// Waits until everything appended so far is on disk. A node calls this when it stops, so the journal is complete by
    /// the time it reports itself stopped; without it the last records, the stop itself among them, were still in the
    /// queue when the process ended.
    /// </summary>
    public async Task FlushAsync(TimeSpan? timeout = null)
    {
        long target = Interlocked.Read(ref _enqueued);
        DateTime deadline = DateTime.UtcNow + (timeout ?? DefaultFlushTimeout);
        while (Interlocked.Read(ref _written) + Interlocked.Read(ref _dropped) < target && DateTime.UtcNow < deadline)
        {
            await Task.Delay(FlushPoll).ConfigureAwait(false);
        }
    }

    /// <summary>Everything the journal holds for the given days, oldest first. A line that cannot be read is skipped.</summary>
    public IEnumerable<JournalRecord> Read(DateOnly? from = null, DateOnly? to = null)
    {
        foreach (string path in System.IO.Directory.EnumerateFiles(JournalDirectory, "*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                || (from is { } f && day < f) || (to is { } t && day > t))
            {
                continue;
            }

            foreach (string line in ReadLines(path))
            {
                JournalRecord? record = null;
                try
                {
                    record = JsonSerializer.Deserialize<JournalRecord>(line, Lines);
                }
                catch (JsonException)
                {
                }

                if (record is not null)
                {
                    yield return record;
                }
            }
        }
    }

    // A journal being written to is open for append; reading it must not fail because of that.
    private static IEnumerable<string> ReadLines(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private void Prune()
    {
        if (_config.JournalDays <= 0)
        {
            return;
        }

        DateOnly oldest = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-_config.JournalDays);
        foreach (string path in System.IO.Directory.EnumerateFiles(JournalDirectory, "*.jsonl"))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) && day < oldest)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(e, "Old journal file {Path} could not be deleted", path);
                }
            }
        }
    }

    // ----- Saved state -----

    public void SaveState(RuntimeModuleId runtimeModuleId, IDictionary<string, byte[]> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string path = StatePath(runtimeModuleId);
        try
        {
            Dictionary<string, string> encoded = state.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value), StringComparer.Ordinal);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(encoded, Lines), Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogError(e, "State of {RuntimeModuleId} could not be saved to {Path}", runtimeModuleId, path);
        }
    }

    /// <summary>The state saved for that runtimeModule, or null when there is none or it cannot be read.</summary>
    public IDictionary<string, byte[]>? LoadState(RuntimeModuleId runtimeModuleId)
    {
        string path = StatePath(runtimeModuleId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            Dictionary<string, string>? encoded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8), Lines);
            return encoded?.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _log.LogError(e, "State of {RuntimeModuleId} could not be read from {Path}; it starts without it", runtimeModuleId, path);
            return null;
        }
    }

    public bool RestoresState => _config.RestoreState;

    /// <summary>
    /// State belonging to the node itself rather than to any of its runtimeModules - what its clock was doing, and whatever
    /// else has to outlive the process without having an owner that can be asked for it.
    ///
    /// <para>
    /// Kept apart from runtimeModule state rather than filed under a made-up runtimeModule id, so that a strategy can never be
    /// named the same thing as a part of the node and silently read its state.
    /// </para>
    /// </summary>
    public void SaveComponentState(string component, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentNullException.ThrowIfNull(json);
        string path = ComponentPath(component);
        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogError(e, "State of {Component} could not be saved to {Path}", component, path);
        }
    }

    /// <summary>What was saved for that part of the node, or null where there is none or it cannot be read.</summary>
    public string? LoadComponentState(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        string path = ComponentPath(component);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError(e, "State of {Component} could not be read from {Path}; it starts without it", component, path);
            return null;
        }
    }

    private string StatePath(RuntimeModuleId runtimeModuleId) => Path.Combine(StateDirectory, Sanitize(runtimeModuleId.Value) + ".json");

    private string ComponentPath(string component) => Path.Combine(StateDirectory, "node." + Sanitize(component) + ".json");

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    public async ValueTask DisposeAsync()
    {
        _queue.CompleteAdding();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "The journal writer stopped with an error");
        }

        _queue.Dispose();
    }
}
