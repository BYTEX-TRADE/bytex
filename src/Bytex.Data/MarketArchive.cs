using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Parquet.Serialization;

namespace Bytex.Data;

/// <summary>
/// Market data stored in opaque Parquet segments with immutable commit manifests.
///
/// <para>
/// Where those objects live is the store's business: a directory on this machine by default, or object storage for a
/// catalog larger than one machine's disk. The layout, the names and every guard over them are the same either way,
/// because they are this class's and not the file system's.
/// </para>
/// </summary>
public sealed class MarketArchive
{
    private const string InstrumentsDir = "instruments";
    private const string QuotesDir = "quotes";
    private const string TradesDir = "trades";
    private const string BarsDir = "bars";
    private const string DeltasDir = "book_deltas";
    private const string DepthDir = "book_depth";
    private const string FundingDir = "funding";

    /// <summary>
    /// The most records this catalog puts in one file.
    ///
    /// <para>
    /// A file is the unit a read holds in memory: <c>StreamAsync</c> takes one file at a time, but every row in that
    /// file is materialised before the first is yielded, so an unbounded file means an unbounded read however lazy
    /// the caller is. Sized from the heaviest kind - 25-level book depth, measured at about 3,011 bytes a snapshot -
    /// which puts roughly 300 MB of records in a full file, and about twice that at the moment of a read, where the
    /// rows and the mapped records are both alive. A full instrument-day of depth becomes sixteen files instead of
    /// one 4.3 GiB file that nothing could open.
    /// </para>
    ///
    /// <para>
    /// It bounds a file and not a write: handing this a day in one call still builds that day in memory first. What
    /// a caller passes in is the caller's to limit.
    /// </para>
    /// </summary>
    public const int MaxRowsPerFile = 100_000;

    private readonly IObjectStore _store;
    private readonly int _maxRowsPerFile;
    private readonly ArchiveJournal _journal;

    /// <summary>
    /// A catalog at a location: a directory on this machine, or <c>s3://bucket/prefix</c> where a backend for that
    /// scheme has been loaded. One string names a catalog everywhere one is asked for, and this is what reads it.
    /// </summary>
    public MarketArchive(string rootPath)
        : this(ObjectStores.Open(rootPath))
    {
    }

    /// <summary>A catalog wherever the given store keeps things.</summary>
    public MarketArchive(IObjectStore store)
        : this(store, MaxRowsPerFile)
    {
    }

    /// <summary>
    /// A catalog with a different file bound, so that a test can cross a file boundary without writing a hundred
    /// thousand records to do it. Not public: the bound is a property of the format, not a knob anyone asked for.
    /// </summary>
    internal MarketArchive(IObjectStore store, int maxRowsPerFile)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRowsPerFile, 1);
        _store = store;
        _journal = new ArchiveJournal(store);
        _maxRowsPerFile = maxRowsPerFile;
    }

    /// <summary>Where this catalog is, for a message: a full path for a local one, a bucket and prefix otherwise.</summary>
    public string RootPath => _store.Location;

    /// <summary>The store this catalog reads and writes through.</summary>
    public IObjectStore Store => _store;

    // ----- Instruments -----

    public async Task WriteInstrumentsAsync(IEnumerable<Instrument> instruments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        foreach (Instrument instrument in instruments)
        {
            await _store.WriteTextAsync(InstrumentKey(instrument.Id.Value), InstrumentJson.Serialize(instrument), ct).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<Instrument> Instruments(Venue? venue = null)
    {
        List<Instrument> result = new();
        foreach (StoredObject stored in _store.List(InstrumentsDir))
        {
            if (!stored.Key.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            Instrument instrument = InstrumentJson.Deserialize(_store.ReadText(stored.Key));
            if (venue is null || instrument.Venue == venue.Value)
            {
                result.Add(instrument);
            }
        }

        return result;
    }

    public Instrument? Instrument(MarketKey id)
    {
        string key = InstrumentKey(id.Value);
        return _store.Exists(key) ? InstrumentJson.Deserialize(_store.ReadText(key)) : null;
    }

    private static string InstrumentKey(string id) => $"{InstrumentsDir}/{SafeName(id)}.json";

    // ----- Writing -----

    public Task WriteQuoteTicksAsync(IEnumerable<QuoteTick> ticks, CancellationToken ct = default) =>
        WriteGroupedAsync(ticks, t => t.MarketKey.Value, t => t.CreatedTime, QuotesDir, QuoteRow.From, ct);

    public Task WriteTradeTicksAsync(IEnumerable<TradeTick> ticks, CancellationToken ct = default) =>
        WriteGroupedAsync(ticks, t => t.MarketKey.Value, t => t.CreatedTime, TradesDir, TradeRow.From, ct);

    public Task WriteBarsAsync(IEnumerable<Bar> bars, CancellationToken ct = default) =>
        WriteGroupedAsync(bars, b => b.CandleSeries.ToString(), b => b.CreatedTime, BarsDir, BarRow.From, ct);

    public Task WriteOrderBookDeltasAsync(IEnumerable<OrderBookDelta> deltas, CancellationToken ct = default) =>
        WriteGroupedAsync(deltas, d => d.MarketKey.Value, d => d.CreatedTime, DeltasDir, DeltaRow.From, ct);

    public Task WriteFundingRatesAsync(IEnumerable<FundingRateUpdate> rates, CancellationToken ct = default) =>
        WriteGroupedAsync(rates, r => r.MarketKey.Value, r => r.CreatedTime, FundingDir, FundingRow.From, ct);

    /// <summary>
    /// Order-book depth: one row per snapshot, each carrying its whole ladder.
    ///
    /// <para>
    /// <b>Wide rows and not one row per level.</b> A day of twenty-five-level snapshots for one instrument is about
    /// 1.5 million of them; normalised to a row per level it is 76 million, which is the same explosion that makes
    /// a venue's incremental feed unusable for a run. One row per snapshot keeps it at the order of magnitude a run
    /// already handles.
    /// </para>
    ///
    /// <para>
    /// <b>The ladder is a list column, so its length is its own.</b> Nothing here knows how many levels a source
    /// publishes - twenty-five is a property of one vendor's dataset, not of depth - and a schema that encoded a
    /// count would be that number written into the storage layout. A snapshot written at N levels reads back at N,
    /// which also means a truncated ladder stays truncated: a book eleven deep does not come back with fourteen
    /// levels at zero, where a fill would find them.
    /// </para>
    /// </summary>
    public Task WriteOrderBookDepthAsync(IEnumerable<OrderBookDepth> depth, CancellationToken ct = default) =>
        WriteGroupedAsync(depth, d => d.MarketKey.Value, d => d.CreatedTime, DepthDir, DepthRow.From, ct);

    /// <summary>
    /// Writes a mixed batch, and REFUSES what it cannot store rather than dropping it.
    ///
    /// <para>
    /// It used to run five type filters and return success, so anything else in the batch was discarded in silence -
    /// no error, no count, nothing to notice. That was every kind of depth, both price updates, a signal and custom
    /// data: a caller handing this a list got back a promise it had not kept, and the data that went missing was
    /// missing rather than visibly wrong, which is the one failure shape this catalog keeps paying for.
    /// </para>
    ///
    /// <para>
    /// Batched deltas are unrolled rather than refused, because their contents are exactly what the delta writer
    /// already stores. Everything the catalog genuinely has no home for is named, with a count, and the call fails.
    /// </para>
    /// </summary>
    public async Task WriteAsync(IEnumerable<IData> data, CancellationToken ct = default)
    {
        List<IData> list = data.ToList();
        await WriteQuoteTicksAsync(list.OfType<QuoteTick>(), ct).ConfigureAwait(false);
        await WriteTradeTicksAsync(list.OfType<TradeTick>(), ct).ConfigureAwait(false);
        await WriteBarsAsync(list.OfType<Bar>(), ct).ConfigureAwait(false);
        await WriteFundingRatesAsync(list.OfType<FundingRateUpdate>(), ct).ConfigureAwait(false);
        await WriteOrderBookDepthAsync(list.OfType<OrderBookDepth>(), ct).ConfigureAwait(false);

        // A batch of deltas is deltas, so it is flattened into them rather than turned away.
        await WriteOrderBookDeltasAsync(
            list.OfType<OrderBookDelta>().Concat(list.OfType<OrderBookDeltas>().SelectMany(b => b.Deltas)),
            ct).ConfigureAwait(false);

        List<IData> unstorable = [.. list.Where(d => d is not (QuoteTick or TradeTick or Bar or FundingRateUpdate or OrderBookDepth or OrderBookDelta or OrderBookDeltas))];
        if (unstorable.Count > 0)
        {
            string kinds = string.Join(", ", unstorable.GroupBy(d => d.GetType().Name).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}"));
            throw new NotSupportedException(
                $"This catalog has nowhere to put {kinds}. It stores quotes, trades, bars, funding rates, book deltas "
                + "and book depth. Nothing was dropped silently - the rest of the batch was written and this is the "
                + "part that was not, so write these elsewhere or leave them out of the batch.");
        }
    }

    private async Task WriteGroupedAsync<T, TRow>(IEnumerable<T> items, Func<T, string> key, Func<T, UnixNanos> ts, string kind, Func<T, TRow> map, CancellationToken ct) where TRow : class, new()
    {
        foreach (IGrouping<string, T> group in items.GroupBy(key))
        {
            List<T> sorted = group.OrderBy(ts).ToList();
            if (sorted.Count == 0)
            {
                continue;
            }

            string prefix = $"{kind}/{SafeName(group.Key)}";
            List<ArchiveSegment> segments = [];
            for (int offset = 0; offset < sorted.Count; offset += _maxRowsPerFile)
            {
                int length = Math.Min(_maxRowsPerFile, sorted.Count - offset);

                // Manifest ranges let a query skip segments outside its window without opening their bytes.
                long start = ts(sorted[offset]).Value;
                long end = ts(sorted[offset + length - 1]).Value;

                // Mapped a file at a time, so the row objects for a whole write are never all alive together.
                List<TRow> rows = new(length);
                for (int i = offset; i < offset + length; i++)
                {
                    rows.Add(map(sorted[i]));
                }

                segments.Add(await _journal.StageAsync(prefix, start, end, length, segments.Count,
                    stream => ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: ct), ct).ConfigureAwait(false));
            }
            await _journal.PublishAsync(prefix, segments, [], ct).ConfigureAwait(false);
        }
    }

    // ----- Reading -----

    public Task<IReadOnlyList<QuoteTick>> QuoteTicksAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<QuoteRow, QuoteTick>(QuotesDir, marketKey.Value, start, end, r => r.ToTick(marketKey), t => t.CreatedTime, ct);

    public Task<IReadOnlyList<TradeTick>> TradeTicksAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<TradeRow, TradeTick>(TradesDir, marketKey.Value, start, end, r => r.ToTick(marketKey), t => t.CreatedTime, ct);

    public Task<IReadOnlyList<Bar>> BarsAsync(CandleSeries candleSeries, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<BarRow, Bar>(BarsDir, candleSeries.ToString(), start, end, r => r.ToBar(candleSeries), b => b.CreatedTime, ct);

    public Task<IReadOnlyList<OrderBookDelta>> OrderBookDeltasAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<DeltaRow, OrderBookDelta>(DeltasDir, marketKey.Value, start, end, r => r.ToDelta(marketKey), d => d.CreatedTime, ct);

    public Task<IReadOnlyList<FundingRateUpdate>> FundingRatesAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<FundingRow, FundingRateUpdate>(FundingDir, marketKey.Value, start, end, r => r.ToUpdate(marketKey), f => f.CreatedTime, ct);

    /// <summary>
    /// Order-book depth in time order. A snapshot written at N levels comes back at N levels, because the ladder is
    /// stored as a list and not as a fixed width - a book that was eleven deep is eleven deep again, rather than
    /// eleven levels followed by fourteen at zero.
    /// </summary>
    public Task<IReadOnlyList<OrderBookDepth>> OrderBookDepthAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        ReadAsync<DepthRow, OrderBookDepth>(DepthDir, marketKey.Value, start, end, r => r.ToDepth(marketKey), d => d.CreatedTime, ct);

    /// <summary>
    /// Quotes in time order, one file at a time, without holding the period.
    /// <para>
    /// The list-returning method beside it reads the same data and keeps it all; this one is for a caller that means
    /// to consume as it goes. See <see cref="StreamAsync"/> for what it guarantees and what it refuses to guess.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<QuoteTick> StreamQuoteTicksAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        StreamAsync<QuoteRow, QuoteTick>(QuotesDir, marketKey.Value, start, end, r => r.ToTick(marketKey), t => t.CreatedTime, ct);

    /// <inheritdoc cref="StreamQuoteTicksAsync"/>
    public IAsyncEnumerable<TradeTick> StreamTradeTicksAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        StreamAsync<TradeRow, TradeTick>(TradesDir, marketKey.Value, start, end, r => r.ToTick(marketKey), t => t.CreatedTime, ct);

    /// <inheritdoc cref="StreamQuoteTicksAsync"/>
    public IAsyncEnumerable<Bar> StreamBarsAsync(CandleSeries candleSeries, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        StreamAsync<BarRow, Bar>(BarsDir, candleSeries.ToString(), start, end, r => r.ToBar(candleSeries), b => b.CreatedTime, ct);

    /// <inheritdoc cref="StreamQuoteTicksAsync"/>
    public IAsyncEnumerable<OrderBookDelta> StreamOrderBookDeltasAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        StreamAsync<DeltaRow, OrderBookDelta>(DeltasDir, marketKey.Value, start, end, r => r.ToDelta(marketKey), d => d.CreatedTime, ct);

    /// <inheritdoc cref="StreamQuoteTicksAsync"/>
    public IAsyncEnumerable<FundingRateUpdate> StreamFundingRatesAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        StreamAsync<FundingRow, FundingRateUpdate>(FundingDir, marketKey.Value, start, end, r => r.ToUpdate(marketKey), f => f.CreatedTime, ct);

    /// <inheritdoc cref="StreamQuoteTicksAsync"/>
    public IAsyncEnumerable<OrderBookDepth> StreamOrderBookDepthAsync(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, CancellationToken ct = default) =>
        StreamAsync<DepthRow, OrderBookDepth>(DepthDir, marketKey.Value, start, end, r => r.ToDepth(marketKey), d => d.CreatedTime, ct);

    /// <summary>
    /// The same data <see cref="ReadAsync"/> returns, yielded rather than collected.
    ///
    /// <para>
    /// <b>What it holds.</b> One file at a time. Records are sorted within a file, because records sharing a
    /// timestamp have a meaning in their order - the two hundred deltas of one book snapshot are that snapshot - and
    /// that sort is bounded by a file rather than by the period asked for. Files are read in the order their names
    /// put them, which is the order they cover.
    /// </para>
    ///
    /// <para>
    /// <b>What it will not guess.</b> The list method sorts everything it has read, so a catalog whose files overlap
    /// in time still comes back in order. Streaming cannot do that without holding everything, which is the point.
    /// So it checks instead: if a record ever arrives older than one already yielded, it stops and names the two
    /// files that disagree. A caller then knows its catalog is not in the shape this promises, rather than receiving
    /// data out of order and finding out through a strategy that behaved oddly.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<TData> StreamAsync<TRow, TData>(string kind, string key, UnixNanos? start, UnixNanos? end, Func<TRow, TData> map, Func<TData, UnixNanos> ts, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) where TRow : class, new()
    {
        UnixNanos highest = default;
        string highestFile = string.Empty;
        bool any = false;

        foreach (ArchiveSegment segment in Segments(kind, key))
        {
            string file = segment.ObjectKey;
            if (!Overlaps(segment, start, end))
            {
                continue;
            }

            List<TData> inFile = new();
            await using (Stream stream = await _journal.OpenVerifiedAsync(segment, ct).ConfigureAwait(false))
            {
                DeserializationResult<TRow> rows = await ParquetSerializer.DeserializeAsync<TRow>(stream, cancellationToken: ct).ConfigureAwait(false);
                foreach (TRow row in rows.Data)
                {
                    TData item = map(row);
                    UnixNanos t = ts(item);
                    if ((start is { } s && t < s) || (end is { } e && t > e))
                    {
                        continue;
                    }

                    inFile.Add(item);
                }
            }

            foreach (TData item in inFile.OrderBy(ts))
            {
                UnixNanos t = ts(item);
                if (any && t < highest)
                {
                    throw new InvalidOperationException(
                        $"The catalog's files for {kind}/{key} overlap in time: {NameOf(file)} holds "
                        + $"{t.Value}, which is older than {highest.Value} already read from "
                        + $"{NameOf(highestFile)}. Streaming reads one file at a time and cannot put an "
                        + "overlapping catalog back in order without holding all of it, which is what it exists to "
                        + "avoid. Consolidate the data for this key, or read it with the list method, which sorts "
                        + "everything it has read.");
                }

                highest = t;
                highestFile = file;
                any = true;
                yield return item;
            }
        }
    }

    private async Task<IReadOnlyList<TData>> ReadAsync<TRow, TData>(string kind, string key, UnixNanos? start, UnixNanos? end, Func<TRow, TData> map, Func<TData, UnixNanos> ts, CancellationToken ct) where TRow : class, new()
    {
        List<TData> result = new();
        foreach (ArchiveSegment segment in Segments(kind, key))
        {
            if (!Overlaps(segment, start, end))
            {
                continue;
            }

            await using Stream stream = await _journal.OpenVerifiedAsync(segment, ct).ConfigureAwait(false);
            DeserializationResult<TRow> rows = await ParquetSerializer.DeserializeAsync<TRow>(stream, cancellationToken: ct).ConfigureAwait(false);
            foreach (TRow row in rows.Data)
            {
                TData item = map(row);
                UnixNanos t = ts(item);
                if (start is { } s && t < s)
                {
                    continue;
                }

                if (end is { } e && t > e)
                {
                    continue;
                }

                result.Add(item);
            }
        }

        // A stable order, because records sharing a timestamp have a meaning in their sequence: the two hundred deltas
        // of one book snapshot are that snapshot, and List.Sort handed them back shuffled.
        return result.OrderBy(ts).ToList();
    }

    /// <summary>Active segments in range order, preserving manifest order for equal timestamps.</summary>
    public IReadOnlyList<ArchiveSegment> Segments(string kind, string key)
    {
        if (!_kinds.Any(k => k.Dir == kind)) { throw new ArgumentException("Unknown archive kind.", nameof(kind)); }
        if (kind == BarsDir) { _ = CandleSeries.Parse(key); } else { _ = MarketKey.Parse(key); }
        return _journal.Active($"{kind}/{SafeName(key)}");
    }

    /// <summary>Retained superseded and uncommitted objects, without deleting recovery evidence.</summary>
    public IReadOnlyList<ArchiveRecoveryObject> RecoveryObjects()
    {
        List<ArchiveRecoveryObject> result = [];
        foreach ((string kind, _) in _kinds)
        {
            foreach (string stream in _store.List(kind).Select(o => Segment(o.Key, kind)).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                string prefix = kind + "/" + stream;
                HashSet<string> committed = new(_journal.Commits(prefix).SelectMany(c => c.Commit.Segments).Select(s => s.ObjectKey), StringComparer.Ordinal);
                HashSet<string> active = new(_journal.Active(prefix).Select(s => s.ObjectKey), StringComparer.Ordinal);
                foreach (StoredObject file in _store.List(prefix).Where(o => o.Key.EndsWith(".parquet", StringComparison.Ordinal)))
                {
                    if (!active.Contains(file.Key))
                    {
                        result.Add(new ArchiveRecoveryObject(file.Key, committed.Contains(file.Key) ? "superseded" : "uncommitted", file.Size));
                    }
                }
            }
        }
        return result;
    }

    private IReadOnlyList<string> DataFiles(string kind, string key) =>
        [.. Segments(kind, key).Select(s => s.ObjectKey)];

    /// <summary>The last part of a key, for a message about one file among several.</summary>
    private static string NameOf(string key) => key[(key.LastIndexOf('/') + 1)..];

    private static bool Overlaps(ArchiveSegment segment, UnixNanos? start, UnixNanos? end)
    {
        if (start is { } s && segment.End < s.Value)
        {
            return false;
        }

        if (end is { } e && segment.Start > e.Value)
        {
            return false;
        }

        return true;
    }

    // ----- Listing -----

    public IReadOnlyList<MarketKey> QuoteTickInstruments() => ListKeys(QuotesDir).Select(MarketKey.Parse).ToList();

    public IReadOnlyList<MarketKey> TradeTickInstruments() => ListKeys(TradesDir).Select(MarketKey.Parse).ToList();

    public IReadOnlyList<CandleSeries> CandleSeriesDefinitions() => ListKeys(BarsDir).Select(CandleSeries.Parse).ToList();

    public IReadOnlyList<MarketKey> OrderBookDeltaInstruments() => ListKeys(DeltasDir).Select(MarketKey.Parse).ToList();

    public IReadOnlyList<MarketKey> OrderBookDepthInstruments() => ListKeys(DepthDir).Select(MarketKey.Parse).ToList();

    /// <summary>Every kind this catalog holds, as the directory it lives in and the name a person uses for it.</summary>
    private static readonly (string Dir, string Label)[] _kinds =
    [
        (QuotesDir, "quotes"),
        (TradesDir, "trades"),
        (BarsDir, "bars"),
        (DeltasDir, "book_deltas"),
        (DepthDir, "book_depth"),
        (FundingDir, "funding"),
    ];

    /// <summary>What this catalog holds, per data set, without counting rows. See <see cref="Entries(bool)"/>.</summary>
    public IReadOnlyList<CatalogEntry> Entries() => Entries(rows: false);

    /// <summary>
    /// What this catalog holds, per data set.
    ///
    /// <para>
    /// Listing only, by default: a name and a size per object, which is one request to an object store however many
    /// files there are. <paramref name="rows"/> asks for the row count as well, and that is a read of every file's
    /// parquet footer - cheap on a disk, a request per file against a bucket - so it is asked for rather than assumed.
    /// The count comes from the footer, never from the data: a file's own record of how many rows it holds.
    /// </para>
    /// </summary>
    public IReadOnlyList<CatalogEntry> Entries(bool rows)
    {
        List<CatalogEntry> entries = new();
        foreach ((string kind, string label) in _kinds)
        {
            foreach (string key in ListKeys(kind))
            {
                long min = long.MaxValue;
                long max = long.MinValue;
                long size = 0;
                int count = 0;
                long counted = 0;
                foreach (ArchiveSegment stored in Segments(kind, key))
                {
                    min = Math.Min(min, stored.Start);
                    max = Math.Max(max, stored.End);

                    size += stored.Bytes;
                    count++;
                    if (rows)
                    {
                        counted += RowsIn(stored.ObjectKey);
                    }
                }

                bool ranged = min != long.MaxValue;
                entries.Add(new CatalogEntry(label, key, count, ranged ? new UnixNanos(min) : null, ranged ? new UnixNanos(max) : null, size)
                {
                    Rows = rows ? counted : null,
                });
            }
        }

        return entries;
    }

    /// <summary>
    /// How many rows a stored file holds, from its parquet footer rather than from its data: no column is read and no
    /// row is materialised. A file that cannot be read at all counts as nothing rather than stopping the listing -
    /// saying what a catalog holds is not the command that judges whether it is sound, and `catalog check` is.
    /// </summary>
    private long RowsIn(string key)
    {
        try
        {
            using Stream stream = _store.OpenReadAsync(key).GetAwaiter().GetResult();
            Parquet.ParquetReader reader = Parquet.ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();
            try
            {
                return reader.Metadata?.NumRows ?? 0;
            }
            finally
            {
                reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
#pragma warning disable CA1031 // A listing must not fail because one file is unreadable; catalog check reports that.
        catch (Exception)
#pragma warning restore CA1031
        {
            return 0;
        }
    }

    // ----- Maintenance -----

    /// <summary>
    /// What is wrong with this catalog, said per key rather than discovered later by something that reads it.
    ///
    /// <para>
    /// Reports invalid manifests, missing or corrupt objects, row-count mismatches and overlapping ranges.
    /// Consolidation can resolve overlap; corruption requires investigation, not destructive maintenance.
    /// </para>
    /// </summary>
    public IReadOnlyList<CatalogProblem> Check()
    {
        List<CatalogProblem> problems = new();
        foreach ((string kind, string label) in _kinds)
        {
            foreach (string key in _store.List(kind).Select(o => Segment(o.Key, kind))
                .OfType<string>().Select(UnsafeName).Distinct(StringComparer.Ordinal))
            {
                List<(string File, long Start, long End)> ranges = new();
                try
                {
                    foreach (ArchiveSegment segment in Segments(kind, key))
                    {
                        using Stream stream = _journal.OpenVerifiedAsync(segment, CancellationToken.None).GetAwaiter().GetResult();
                        if (RowsIn(segment.ObjectKey) != segment.Rows)
                        {
                            problems.Add(new CatalogProblem(label, key, NameOf(segment.ObjectKey), "Segment row count differs from its manifest."));
                        }
                        ranges.Add((NameOf(segment.ObjectKey), segment.Start, segment.End));
                    }
                }
                catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or IOException or FormatException)
                {
                    problems.Add(new CatalogProblem(label, key, "manifest", error.Message));
                }

                foreach ((string File, long Start, long End) earlier in ranges)
                {
                    foreach ((string File, long Start, long End) later in ranges)
                    {
                        if (!ReferenceEquals(earlier.File, later.File)
                            && string.CompareOrdinal(earlier.File, later.File) < 0
                            && later.Start <= earlier.End && earlier.Start <= later.End)
                        {
                            problems.Add(new CatalogProblem(label, key, earlier.File,
                                $"This file covers {earlier.Start} to {earlier.End} and {later.File} covers {later.Start} to {later.End}, "
                                + "so they overlap. A streaming read holds one file at a time and cannot put them back in order; consolidate this key."));
                        }
                    }
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Rewrites one key as a single file in time order, dropping records that appear twice.
    ///
    /// <para>
    /// It is the answer to everything <see cref="Check"/> reports: one file cannot overlap itself, its name is a
    /// range by construction, and the duplicates a re-download leaves behind go with it. The whole key is held while
    /// this runs, which is the one place in this class that is true - so it is a deliberate operation a person asks
    /// for rather than something a read does on their behalf.
    /// </para>
    ///
    /// <para>
    /// The new file is written before the old ones are removed, so an interruption leaves the key readable rather
    /// than empty.
    /// </para>
    /// <returns>How many records the key holds afterwards, or zero where there was nothing to consolidate.</returns>
    /// </summary>
    public async Task<int> ConsolidateAsync(string dataKind, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        (string dir, string label) = _kinds.FirstOrDefault(k => string.Equals(k.Label, dataKind, StringComparison.OrdinalIgnoreCase));
        if (label is null)
        {
            throw new ArgumentException($"'{dataKind}' is not a kind this catalog holds: {string.Join(", ", _kinds.Select(k => k.Label))}.", nameof(dataKind));
        }

        string prefix = $"{dir}/{SafeName(key)}";
        string[] existing = [.. DataFiles(dir, key)];
        if (existing.Length <= 1)
        {
            // Nothing to do, and saying so as zero rather than as a count: a key already in one file is not rewritten,
            // so there is no number of records this call is responsible for.
            return 0;
        }

        return await ConsolidateFilesAsync(label, prefix, existing, ct).ConfigureAwait(false);
    }

    private Task<int> ConsolidateFilesAsync(string label, string prefix, string[] existing, CancellationToken ct) => label switch
    {
        "quotes" => RewriteAsync<QuoteRow>(prefix, existing, r => r.CreatedTime, ct),
        "trades" => RewriteAsync<TradeRow>(prefix, existing, r => r.CreatedTime, ct),
        "bars" => RewriteAsync<BarRow>(prefix, existing, r => r.CreatedTime, ct),
        "book_deltas" => RewriteAsync<DeltaRow>(prefix, existing, r => r.CreatedTime, ct),
        "book_depth" => RewriteAsync<DepthRow>(prefix, existing, r => r.CreatedTime, ct),
        "funding" => RewriteAsync<FundingRow>(prefix, existing, r => r.CreatedTime, ct),
        _ => throw new ArgumentException($"'{label}' is not a kind this catalog holds.", nameof(label)),
    };

    /// <summary>
    /// One key's files read, ordered, de-duplicated and written back as a single file.
    ///
    /// <para>
    /// Two records are the same record when every field of them is, which is what a re-downloaded day leaves behind:
    /// the same trades, written twice, in two files that now overlap. Identity is taken from the row's own JSON
    /// rather than from a timestamp, because two genuinely different quotes can share a microsecond and dropping one
    /// of those would be losing data rather than tidying it.
    /// </para>
    /// </summary>
    private async Task<int> RewriteAsync<TRow>(string prefix, string[] existing, Func<TRow, long> ts, CancellationToken ct) where TRow : class, new()
    {
        List<TRow> all = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        IReadOnlyList<ArchiveSegment> active = _journal.Active(prefix);
        foreach (string file in existing)
        {
            await using Stream stream = await _journal.OpenVerifiedAsync(active.Single(s => s.ObjectKey == file), ct).ConfigureAwait(false);
            DeserializationResult<TRow> rows = await ParquetSerializer.DeserializeAsync<TRow>(stream, cancellationToken: ct).ConfigureAwait(false);
            foreach (TRow row in rows.Data)
            {
                if (seen.Add(System.Text.Json.JsonSerializer.Serialize(row)))
                {
                    all.Add(row);
                }
            }
        }

        if (all.Count == 0)
        {
            return 0;
        }

        all = all.OrderBy(ts).ToList();

        // Consolidating obeys the same bound as writing, or it would undo it: merging a chunked key back into one
        // file would put the whole data set into a single read, which is the thing the bound exists to prevent.
        // So this reduces the FILE COUNT to the fewest the bound allows, rather than to one.
        List<ArchiveSegment> pending = [];
        for (int offset = 0; offset < all.Count; offset += _maxRowsPerFile)
        {
            int length = Math.Min(_maxRowsPerFile, all.Count - offset);
            List<TRow> chunk = all.GetRange(offset, length);
            pending.Add(await _journal.StageAsync(prefix, ts(chunk[0]), ts(chunk[^1]), length, pending.Count,
                stream => ParquetSerializer.SerializeAsync(chunk, stream, cancellationToken: ct), ct).ConfigureAwait(false));
        }

        await _journal.PublishAsync(prefix, pending, existing, ct).ConfigureAwait(false);

        return all.Count;
    }

    /// <summary>
    /// The keys one kind holds. Taken from the objects rather than from a directory listing, because object storage has
    /// no directories - a key with nothing under it does not exist there, and a catalog must read the same either way.
    /// </summary>
    private IEnumerable<string> ListKeys(string kind) => _store
        .List(kind)
        .Where(o => o.Key.Contains("/commits/", StringComparison.Ordinal) && o.Key.EndsWith(".json", StringComparison.Ordinal))
        .Select(o => Segment(o.Key, kind))
        .Where(segment => segment is not null)
        .Select(segment => UnsafeName(segment!))
        .Distinct(StringComparer.Ordinal);

    /// <summary>The key part of <c>{kind}/{key}/{file}</c>, or null for an object that is not laid out that way.</summary>
    private static string? Segment(string key, string kind)
    {
        string[] parts = key.Split('/');
        return parts.Length >= 3 && string.Equals(parts[0], kind, StringComparison.Ordinal) ? parts[1] : null;
    }

    // Percent-encoded rather than flattened to underscores: three different characters all became '_', so the name
    // could not be mapped back, and bx-market:v2/SIM/EUR%2FUSD was listed as, and shared a directory with, bx-market:v2/SIM/EUR_USD.
    private static string SafeName(string key) => Bytex.Core.Serialization.ObjectKeyCodec.Encode(key);

    private static string UnsafeName(string name) => Bytex.Core.Serialization.ObjectKeyCodec.Decode(name);

    // ----- Row types -----

    public sealed class QuoteRow
    {
        public long EventTime { get; set; }

        public long CreatedTime { get; set; }

        public decimal Bid { get; set; }

        public decimal Ask { get; set; }

        public decimal BidSize { get; set; }

        public decimal AskSize { get; set; }

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public static QuoteRow From(QuoteTick t) => new()
        {
            EventTime = t.EventTime.Value,
            CreatedTime = t.CreatedTime.Value,
            Bid = t.Bid.Value,
            Ask = t.Ask.Value,
            BidSize = t.BidSize.Value,
            AskSize = t.AskSize.Value,
            PricePrecision = t.Bid.Precision,
            SizePrecision = t.BidSize.Precision,
        };

        public QuoteTick ToTick(MarketKey id) => new(id, new Price(Bid, PricePrecision), new Price(Ask, PricePrecision), new Quantity(BidSize, SizePrecision), new Quantity(AskSize, SizePrecision), new UnixNanos(EventTime), new UnixNanos(CreatedTime));
    }

    public sealed class TradeRow
    {
        public long EventTime { get; set; }

        public long CreatedTime { get; set; }

        public decimal Price { get; set; }

        public decimal Size { get; set; }

        public int Aggressor { get; set; }

        public string TradeId { get; set; } = string.Empty;

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public static TradeRow From(TradeTick t) => new()
        {
            EventTime = t.EventTime.Value,
            CreatedTime = t.CreatedTime.Value,
            Price = t.Price.Value,
            Size = t.Size.Value,
            Aggressor = (int)t.Aggressor,
            TradeId = t.TradeId.Value,
            PricePrecision = t.Price.Precision,
            SizePrecision = t.Size.Precision,
        };

        public TradeTick ToTick(MarketKey id) => new(id, new Price(Price, PricePrecision), new Quantity(Size, SizePrecision), (AggressorSide)Aggressor, new TradeId(TradeId), new UnixNanos(EventTime), new UnixNanos(CreatedTime));
    }

    public sealed class BarRow
    {
        public long EventTime { get; set; }

        public long CreatedTime { get; set; }

        public decimal Open { get; set; }

        public decimal High { get; set; }

        public decimal Low { get; set; }

        public decimal Close { get; set; }

        public decimal Volume { get; set; }

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public bool IsRevision { get; set; }

        public static BarRow From(Bar b) => new()
        {
            EventTime = b.EventTime.Value,
            CreatedTime = b.CreatedTime.Value,
            Open = b.Open.Value,
            High = b.High.Value,
            Low = b.Low.Value,
            Close = b.Close.Value,
            Volume = b.Volume.Value,
            PricePrecision = b.Close.Precision,
            SizePrecision = b.Volume.Precision,
            IsRevision = b.IsRevision,
        };

        public Bar ToBar(CandleSeries candleSeries) => new(candleSeries, new Price(Open, PricePrecision), new Price(High, PricePrecision), new Price(Low, PricePrecision), new Price(Close, PricePrecision), new Quantity(Volume, SizePrecision), new UnixNanos(EventTime), new UnixNanos(CreatedTime), IsRevision);
    }

    /// <summary>
    /// A published funding rate. The next funding time is what the venue said it would be, and zero stands for "the
    /// venue did not say": a time of zero is not a time anybody means.
    /// </summary>
    public sealed class FundingRow
    {
        public long EventTime { get; set; }

        public long CreatedTime { get; set; }

        public decimal Rate { get; set; }

        public long NextFundingTime { get; set; }

        public static FundingRow From(FundingRateUpdate update) => new()
        {
            EventTime = update.EventTime.Value,
            CreatedTime = update.CreatedTime.Value,
            Rate = update.Rate,
            NextFundingTime = update.NextFundingTime?.Value ?? 0L,
        };

        public FundingRateUpdate ToUpdate(MarketKey marketKey) =>
            new(marketKey, Rate, NextFundingTime == 0L ? null : new UnixNanos(NextFundingTime), new UnixNanos(EventTime), new UnixNanos(CreatedTime));
    }

    public sealed class DeltaRow
    {
        public long EventTime { get; set; }

        public long CreatedTime { get; set; }

        public int Action { get; set; }

        public int Side { get; set; }

        public decimal Price { get; set; }

        public decimal Size { get; set; }

        public long OrderId { get; set; }

        public byte Flags { get; set; }

        public long Sequence { get; set; }

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public static DeltaRow From(OrderBookDelta d) => new()
        {
            EventTime = d.EventTime.Value,
            CreatedTime = d.CreatedTime.Value,
            Action = (int)d.Action,
            Side = (int)d.Order.Side,
            Price = d.Order.Price.Value,
            Size = d.Order.Size.Value,
            OrderId = (long)d.Order.OrderId,
            Flags = (byte)d.Flags,
            Sequence = (long)d.Sequence,
            PricePrecision = d.Order.Price.Precision,
            SizePrecision = d.Order.Size.Precision,
        };

        public OrderBookDelta ToDelta(MarketKey id) => new(id, (BookAction)Action, new BookOrder((OrderSide)Side, new Price(Price, PricePrecision), new Quantity(Size, SizePrecision), (ulong)OrderId), (RecordFlags)Flags, (ulong)Sequence, new UnixNanos(EventTime), new UnixNanos(CreatedTime));
    }

    /// <summary>
    /// One snapshot of an order book, whole, in one row.
    ///
    /// <para>
    /// <b>The ladders are list columns.</b> Parquet holds a repeated field per row, so a side's length is its own and
    /// nothing in this schema knows how many levels a source publishes - twenty-five belongs to one vendor's dataset,
    /// not to depth. That is also what keeps a truncated ladder truncated: a fixed-width schema would need a value in
    /// every column, and a zero there is a price a fill can reach.
    /// </para>
    ///
    /// <para>
    /// Precisions are stored once per row rather than per level, because every level of one instrument's book is
    /// quoted and sized at the instrument's own precision, and a row repeating them twenty-five times a side would be
    /// fifty copies of two bytes to say one thing.
    /// </para>
    /// </summary>
    public sealed class DepthRow
    {
        public long EventTime { get; set; }

        public long CreatedTime { get; set; }

        public byte Flags { get; set; }

        public long Sequence { get; set; }

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public List<decimal>? BidPrices { get; set; }

        public List<decimal>? BidSizes { get; set; }

        public List<int>? BidCounts { get; set; }

        public List<decimal>? AskPrices { get; set; }

        public List<decimal>? AskSizes { get; set; }

        public List<int>? AskCounts { get; set; }

        public static DepthRow From(OrderBookDepth d) => new()
        {
            EventTime = d.EventTime.Value,
            CreatedTime = d.CreatedTime.Value,
            Flags = (byte)d.Flags,
            Sequence = (long)d.Sequence,

            // A book with nothing on either side would have no precision to record; zero is right for it, because
            // there is no level to read back at any precision.
            PricePrecision = d.Bids.Count > 0 ? d.Bids[0].Price.Precision : d.Asks.Count > 0 ? d.Asks[0].Price.Precision : (byte)0,
            SizePrecision = d.Bids.Count > 0 ? d.Bids[0].Size.Precision : d.Asks.Count > 0 ? d.Asks[0].Size.Precision : (byte)0,
            BidPrices = [.. d.Bids.Select(l => l.Price.Value)],
            BidSizes = [.. d.Bids.Select(l => l.Size.Value)],
            BidCounts = [.. d.Bids.Select(l => l.Count)],
            AskPrices = [.. d.Asks.Select(l => l.Price.Value)],
            AskSizes = [.. d.Asks.Select(l => l.Size.Value)],
            AskCounts = [.. d.Asks.Select(l => l.Count)],
        };

        public OrderBookDepth ToDepth(MarketKey marketKey) => new(
            marketKey,
            Ladder(BidPrices, BidSizes, BidCounts),
            Ladder(AskPrices, AskSizes, AskCounts),
            (RecordFlags)Flags,
            (ulong)Sequence,
            new UnixNanos(EventTime),
            new UnixNanos(CreatedTime));

        /// <summary>
        /// One side, at the length it was written. A column that came back null or short is a side that had that many
        /// levels, so the ladder ends where the shortest of the three does rather than being padded to anything.
        /// </summary>
        private List<BookLevel> Ladder(List<decimal>? prices, List<decimal>? sizes, List<int>? counts)
        {
            if (prices is null || sizes is null)
            {
                return [];
            }

            int levels = Math.Min(prices.Count, sizes.Count);
            List<BookLevel> ladder = new(levels);
            for (int i = 0; i < levels; i++)
            {
                ladder.Add(new BookLevel(
                    new Price(prices[i], PricePrecision),
                    new Quantity(sizes[i], SizePrecision),
                    counts is not null && i < counts.Count ? counts[i] : 1));
            }

            return ladder;
        }
    }
}

public sealed record CatalogEntry(string Kind, string Key, int FileCount, UnixNanos? Start, UnixNanos? End, long SizeBytes)
{
    /// <summary>
    /// How many rows this data set holds, or null when nobody asked. Counting is a read of every file's footer, so
    /// <see cref="MarketArchive.Entries(bool)"/> only does it when asked; null therefore means "not counted" and never
    /// "empty". An empty data set counts zero.
    /// </summary>
    public long? Rows { get; init; }
}

/// <summary>Something about a catalog that will stop a reader, said where it is rather than where it is felt.</summary>
public sealed record CatalogProblem(string Kind, string Key, string File, string Detail);

public sealed record ArchiveRecoveryObject(string ObjectKey, string State, long Bytes);
