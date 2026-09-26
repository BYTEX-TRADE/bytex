using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests;

// Why: VenueMarginTests forbids an adapter inventing a margin figure. This file is the other half of the same
// problem - that a correct figure and an invented one look identical once they are a decimal. 0.05 read from a venue
// and 0.05 chosen by an adapter are the same number and a different claim, and for two releases every report
// presented the second as the first.
//
// It matters most for what is already stored. Reading the venue fixed every run made from now on and could not
// touch a saved one, so somebody comparing a saved result with a fresh one on the same strategy, period and venue
// sees them disagree with nothing to say why - a corrected input and a broken engine look the same from there. The
// provenance therefore travels with the instrument, into the catalog and into the result of the run that used it.
//
// What is guarded here is not "the venues are right today" but that no venue can set a margin without saying where
// it got it, which is what the venues arriving next have to satisfy without being told.
public sealed class MarginSourceTests
{
    [Fact]
    public void Every_instrument_a_venue_builds_says_where_its_margin_came_from()
    {
        // Counted per file rather than asserted per venue, because the failure this catches is a NEW instrument
        // shape - a second family, a dated contract beside a perpetual - built by copying a spec and leaving the
        // provenance behind. That instrument would report Unrecorded, which reads as "stored before the marker
        // existed": the one answer that is false about an instrument built today.
        foreach (string venue in Repo.ShippedVenues())
        {
            foreach (string file in Repo.SourceFiles(venue))
            {
                string source = File.ReadAllText(file);
                int specs = _spec.Matches(source).Count;
                int stated = Occurrences(source, "MarginSource =");

                Assert.True(
                    specs == stated,
                    $"{Path.GetFileName(file)} builds {specs} instrument spec(s) and states a margin source {stated} time(s). "
                    + "Every instrument a venue builds has to say where its margin came from - the figure cannot, and an "
                    + "instrument that says nothing reports Unrecorded, which claims it was stored before the marker "
                    + "existed rather than that this adapter never said.");
            }
        }
    }

    [Fact]
    public void No_adapter_reports_the_answer_that_means_nobody_recorded_it()
    {
        // Unrecorded is the default so that an instrument from an old catalog, or one built by hand in a test or a
        // host, says so. An adapter writing it would be recording that nothing was recorded, at the one place where
        // something is.
        foreach (string venue in Repo.ShippedVenues())
        {
            foreach (string file in Repo.SourceFiles(venue))
            {
                Assert.DoesNotContain("MarginSource.Unrecorded", File.ReadAllText(file), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Nothing_recorded_is_what_an_unset_margin_source_means()
    {
        // Pinned as a value and not only as a name. The whole scheme rests on the default being this member: a
        // missing JSON field, a spec built without the property and an instrument from a catalog written last month
        // all land on default(MarginSource), and if that were ever a venue answer they would all be claiming it.
        Assert.Equal(MarginSource.Unrecorded, default);
        Assert.Equal(0, (int)MarginSource.Unrecorded);
    }

    // ----- what each venue actually reports, through its own provider -----

    [Fact]
    public async Task Binance_reports_a_venue_wide_default_because_that_is_what_it_publishes_without_a_key()
    {
        // The case the marker exists for. This venue publishes requiredMarginPercent on every contract and it is the
        // same 5.0 on all 909 of them - a venue-wide default, not the minimum it takes, which its own arithmetic
        // proves: 5 percent supports 20x where the venue grants 125x. The figure is sourced and still coarse, so a
        // result carrying it must not look like one carrying the brackets.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo)
            .Handle);

        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures, BaseUrlHttp = server.HttpBase }, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.UsdMFutures, null, null);
        await provider.LoadAllAsync(CancellationToken.None);

        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BINANCE"))!;

        Assert.Equal(MarginSource.VenueWideDefault, perp.MarginSource);

        // And the figure it goes with, so that the two cannot drift apart: the marker is about THIS number.
        Assert.Equal(0.05m, perp.MarginInit);
    }

    [Fact]
    public async Task Binances_coin_margined_family_reports_the_same_thing_for_the_same_reason()
    {
        // Measured 2026-09-25: all 30 contracts publish 5.0000 and 2.5000, from the 100-USD BTCUSD perpetual to the
        // 10-USD altcoin quarterlies. A figure identical across every contract of a market is not what that market
        // requires of each of them.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/dapi/v1/exchangeInfo", BinancePayloads.CoinMExchangeInfo)
            .Handle);

        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.CoinMFutures, BaseUrlHttp = server.HttpBase }, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.CoinMFutures, null, null);
        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Equal(MarginSource.VenueWideDefault, provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!.MarginSource);
    }

    [Fact]
    public async Task A_spot_pair_reports_that_nothing_is_borrowed_rather_than_that_nothing_was_read()
    {
        // Zero margin on a cash pair is a fact, and it has to be distinguishable from zero because nobody looked.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo)
            .Handle);

        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.Spot, BaseUrlHttp = server.HttpBase }, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.Spot, null, null);
        await provider.LoadAllAsync(CancellationToken.None);

        Instrument pair = provider.GetAll()[0];

        Assert.Equal(0m, pair.MarginInit);
        Assert.Equal(MarginSource.NotMargined, pair.MarginSource);
    }

    [Fact]
    public async Task A_symbol_publishing_no_margin_at_all_reports_the_engines_own_figure()
    {
        // The third of this venue's three answers, and the only one that is the engine's. It cannot come from a
        // recorded fixture: the venue publishes these fields on every symbol, so a payload without them is not a
        // recording of anything - it is this one with the fields taken out, which is exactly the shape the adapter's
        // fallback exists for and the shape nothing exercised.
        string stripped = System.Text.RegularExpressions.Regex.Replace(
            BinancePayloads.FuturesExchangeInfo,
            @"""[a-zA-Z]*[mM]arginPercent""\s*:\s*""[0-9.]+""\s*,",
            string.Empty);

        Assert.DoesNotContain("requiredMarginPercent", stripped, StringComparison.Ordinal);

        await using LoopbackServer server = new(new Routes()
            .On("GET", "/fapi/v1/exchangeInfo", stripped)
            .Handle);

        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures, BaseUrlHttp = server.HttpBase }, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.UsdMFutures, null, null);
        await provider.LoadAllAsync(CancellationToken.None);

        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BINANCE"))!;

        // The same 0.05 as when the venue published it, which is the whole problem the marker solves: the figure is
        // identical and the claim is not.
        Assert.Equal(0.05m, perp.MarginInit);
        Assert.Equal(MarginSource.AdapterDefault, perp.MarginSource);
    }

    [Fact]
    public async Task Bybit_reports_the_figure_it_read_per_contract()
    {
        // The venue this was found on: 0.05 for every contract where its own risk limits give 0.0066 on BTCUSDT,
        // which is where the 7.6x came from. A run made against the risk limits says so here.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.LinearInstrumentsPage1.Replace("cursor-page-2", string.Empty, StringComparison.Ordinal))
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.LinearRiskLimits)
            .Handle);

        using BybitHttp http = new(new BybitDataClientConfig { ProductType = BybitProductType.Linear, BaseUrlHttp = server.HttpBase });
        BybitInstrumentProvider provider = new(http, BybitProductType.Linear);
        await provider.LoadAllAsync(CancellationToken.None);

        Instrument perp = provider.GetAll()[0];

        Assert.Equal(MarginSource.VenuePerContract, perp.MarginSource);
        Assert.True(perp.MarginInit > 0m, "a per-contract answer with no figure behind it would be the silent case");
    }

    [Fact]
    public async Task An_option_reports_a_gap_and_not_an_unmargined_product()
    {
        // Both zeroes, and different facts. A short option IS margined at this venue, by a portfolio calculation the
        // adapter cannot see - the risk-limit endpoint refuses the category and the contract data carries no
        // leverageFilter - so the engine holds no requirement and a liquidation cannot fire on one. Reporting that as
        // "nothing is borrowed", the way spot does, would hide the one thing a reader needs to know.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments)
            .Handle);

        using BybitHttp http = new(new BybitDataClientConfig { ProductType = BybitProductType.Option, BaseUrlHttp = server.HttpBase });
        BybitInstrumentProvider provider = new(http, BybitProductType.Option);
        await provider.LoadAllAsync(CancellationToken.None, new Dictionary<string, string>(StringComparer.Ordinal));

        Instrument option = provider.GetAll()[0];

        Assert.Equal(0m, option.MarginInit);
        Assert.Equal(MarginSource.VenueSilent, option.MarginSource);
        Assert.NotEqual(MarginSource.NotMargined, option.MarginSource);
    }

    [Fact]
    public async Task KuCoin_reports_per_contract_because_it_always_did_read_it()
    {
        // The one shipped venue that never carried an invented figure, and the marker says so rather than leaving it
        // indistinguishable from the two that did.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts)
            .Handle);

        KucoinHttp http = new(new KucoinDataClientConfig { ProductType = KucoinProductType.Futures, BaseUrlHttp = server.HttpBase });
        KucoinFuturesInstrumentProvider provider = new(http);
        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Equal(MarginSource.VenuePerContract, provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!.MarginSource);
    }

    // ----- and it survives the journey a stored run depends on -----

    [Fact]
    public void The_source_survives_the_catalog_and_a_catalog_written_before_it_reads_back_as_unrecorded()
    {
        // The whole point is a STORED run explaining itself, so a provenance that does not survive being written and
        // read is no provenance at all.
        Instrument stated = Perpetual(MarginSource.VenuePerContract);

        Assert.Equal(MarginSource.VenuePerContract, Roundtrip(stated).MarginSource);

        // And the other direction, which is the case every existing installation is in: an instrument written before
        // the field existed has no marginSource in its JSON at all. It must read back as Unrecorded - saying "nobody
        // recorded this" - rather than inheriting whatever member happens to be first.
        string written = Bytex.Data.InstrumentJson.Serialize(stated);
        string withoutTheField = System.Text.RegularExpressions.Regex.Replace(
            written,
            "\"marginSource\"\\s*:\\s*\"[A-Za-z]+\"\\s*,?",
            string.Empty);

        Assert.DoesNotContain("marginSource", withoutTheField, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MarginSource.Unrecorded, Bytex.Data.InstrumentJson.Deserialize(withoutTheField).MarginSource);
    }

    [Fact]
    public void The_source_is_written_as_the_name_a_host_reads_and_not_as_a_number()
    {
        // A host runs this engine as a child process and reads its JSON, so the vocabulary is part of the contract.
        // An integer here would make every reader keep a table of what 4 means, and reordering the enum would
        // silently change what old files say.
        Instrument stated = Perpetual(MarginSource.VenueWideDefault);

        Assert.Contains("\"marginSource\":\"venueWideDefault\"", Bytex.Data.InstrumentJson.Serialize(stated).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static Instrument Perpetual(MarginSource source) => new CryptoPerpetual(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT-PERP.SIM"),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 3,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.001m, 3),
        MarginInit = 0.0066m,
        MarginMaint = 0.0033m,
        MarginSource = source,
    });

    /// <summary>
    /// A spec being CONSTRUCTED, in either spelling C# allows - <c>new InstrumentSpec { ... }</c> and the
    /// target-typed <c>InstrumentSpec spec = new()</c>, which is what one adapter uses. Matching only the first
    /// spelling counted that adapter's spec as zero and failed it for stating a provenance it does state, which is
    /// the wrong direction for a guard to be wrong in: a rule nobody can satisfy gets deleted.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _spec = new(
        @"new\s+InstrumentSpec|InstrumentSpec\s+\w+\s*=\s*new\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int Occurrences(string source, string token)
    {
        int count = 0;
        for (int at = source.IndexOf(token, StringComparison.Ordinal); at >= 0; at = source.IndexOf(token, at + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static Instrument Roundtrip(Instrument instrument) =>
        Bytex.Data.InstrumentJson.Deserialize(Bytex.Data.InstrumentJson.Serialize(instrument));
}
