using Bytex.Adapters.Tardis;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Tardis;

// Why: this vendor names a market per PRODUCT FAMILY, and the map was keyed by venue plus a boolean "is it a
// perpetual". The vendor splits finer than that - binance, binance-futures and binance-delivery are three datasets,
// and bybit-spot, bybit and bybit-options are three more - so two markets a person can already pick were fetched from
// the wrong dataset:
//
//   a Bybit OPTION is neither Swap nor Future, so it fell to the venue key and was read from bybit-spot
//   a Binance COIN-MARGINED contract is a Swap, so it was read from binance-futures, the USD-margined dataset
//
// Neither fails loudly. The vendor answers an unknown combination with nothing, so both look like the vendor having
// no data for that period rather than like this adapter asking the wrong question. That is also why the old fallback
// to a lower-cased venue name had to go: it is right for four of this engine's eight venues and wrong for OKX
// (`okex`) and Gate (`gate-io`), and being wrong is indistinguishable from the vendor being empty.
//
// Every id asserted here was read from the vendor's own published exchange list, not inferred.
public sealed class TardisExchangeMapTests
{
    private static Instrument Instrument(string venue, string symbol, InstrumentClass instrumentClass, bool inverse = false)
    {
        InstrumentSpec spec = new()
        {
            Id = new MarketKey(new Symbol(symbol), new Venue(venue)),
            RawSymbol = new Symbol(symbol),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = instrumentClass,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = inverse ? Currencies.BTC : Currencies.USDT,
            IsInverse = inverse,
            PricePrecision = 1,
            SizePrecision = 3,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.001m, 3),
        };

        return instrumentClass switch
        {
            InstrumentClass.Spot => new CurrencyPair(spec),
            InstrumentClass.Option => new OptionContract(spec, "BTC", OptionKind.Call, new Price(100m, 1), new UnixNanos(1), new UnixNanos(2), null),
            _ => new CryptoPerpetual(spec),
        };
    }

    /// <summary>
    /// <b>A host can ask which dataset a market is published as, without a client and without repeating the rule.</b>
    /// The lookup is two steps in a particular order, and hosts were doing it themselves against the map - two copies
    /// of one rule, and the copy is the one that goes stale the day a venue is added.
    ///
    /// <para>
    /// Asserted against what the adapter really requests, so the public answer and the requested path cannot drift:
    /// if they ever disagree, the copy is back.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("BINANCE", InstrumentClass.Spot, false, "binance")]
    [InlineData("BINANCE", InstrumentClass.Swap, false, "binance-futures")]
    [InlineData("BINANCE", InstrumentClass.Swap, true, "binance-delivery")]
    [InlineData("BYBIT", InstrumentClass.Option, false, "bybit-options")]
    [InlineData("OKX", InstrumentClass.Swap, false, "okex-swap")]
    public async Task A_host_is_told_which_dataset_a_market_is_published_as(string venue, InstrumentClass instrumentClass, bool inverse, string dataset)
    {
        Instrument instrument = Instrument(venue, "BTCUSDT", instrumentClass, inverse);

        Assert.Equal(dataset, new TardisDataClientConfig().DatasetFor(instrument));
        Assert.Equal(dataset, await RequestedExchangeAsync(instrument));
    }

    /// <summary>
    /// And null where nobody has verified a name, which is a different answer from a guess: the client turns that null
    /// into its own refusal, so the one rule has one home and the message has another.
    /// </summary>
    [Fact]
    public void A_market_this_vendor_is_not_set_up_for_answers_null_rather_than_a_guess()
    {
        Assert.Null(new TardisDataClientConfig().DatasetFor(Instrument("MEXC", "BTCUSDT", InstrumentClass.Swap)));
        Assert.Null(new TardisDataClientConfig().DatasetFor(Instrument("KRAKEN", "BTCUSD", InstrumentClass.Swap)));

        // Served venue, unserved family: still null, and not the venue's spot dataset by accident.
        Assert.Null(new TardisDataClientConfig().DatasetFor(Instrument("HYPERLIQUID", "BTCUSDT", InstrumentClass.Spot)));
    }

    /// <summary>The exchange id this adapter actually asked for, taken from the path it requested.</summary>
    private static async Task<string> RequestedExchangeAsync(Instrument instrument)
    {
        await using LoopbackServer server = new(_ => new StubResponse(404, "no such dataset"));
        TestTradingRuntime tradingRuntime = new();
        tradingRuntime.TradingRuntime.Cache.AddInstrument(instrument);

        TardisDataClient client = new(
            new ClientId("TARDIS"),
            new TardisDataClientConfig { ApiKey = "test-key", BaseUrl = server.HttpBase + "/v1" },
            tradingRuntime.Services);

        try
        {
            await client.LoadTradesAsync(instrument.Id, new UnixNanos(1_700_000_000_000_000_000), new UnixNanos(1_700_000_000_000_000_000), null, CancellationToken.None);
        }
        catch (Exception)
        {
            // The vendor is a stub answering 404; what is under test is the address this adapter chose.
        }

        string path = Assert.Single(server.Requests.Select(r => r.Path).Distinct());

        // /v1/<exchange>/trades/yyyy/mm/dd/<symbol>.csv.gz
        return path.Split('/')[2];
    }

    [Fact]
    public async Task A_coin_margined_contract_is_read_from_the_venues_coin_margined_dataset()
    {
        // The defect, in the venue where it costs most: this used to ask binance-futures, the USD-margined dataset,
        // for a contract that is not in it.
        Assert.Equal("binance-delivery", await RequestedExchangeAsync(Instrument("BINANCE", "BTCUSD_PERP", InstrumentClass.Swap, inverse: true)));
    }

    [Fact]
    public async Task A_usd_margined_contract_is_still_read_from_the_usd_margined_dataset()
    {
        // The other half of the same venue, so a change that sent everything to one dataset could not pass.
        Assert.Equal("binance-futures", await RequestedExchangeAsync(Instrument("BINANCE", "BTCUSDT", InstrumentClass.Swap)));
    }

    [Fact]
    public async Task An_option_is_read_from_the_options_dataset_and_not_from_spot()
    {
        Assert.Equal("bybit-options", await RequestedExchangeAsync(Instrument("BYBIT", "BTC-25JUN27-106000-P-USDT", InstrumentClass.Option)));
    }

    [Fact]
    public async Task A_venue_whose_spelling_could_not_be_guessed_uses_the_vendors_own_name()
    {
        // The two the fallback would have got wrong, and the reason the fallback is gone.
        Assert.Equal("okex", await RequestedExchangeAsync(Instrument("OKX", "BTC-USDT", InstrumentClass.Spot)));
        Assert.Equal("gate-io", await RequestedExchangeAsync(Instrument("GATE", "BTC_USDT", InstrumentClass.Spot)));
    }

    [Fact]
    public async Task A_market_this_vendor_does_not_publish_is_refused_by_name()
    {
        // Kraken's futures have no dataset at all - a fact about the vendor, not a gap here - and the refusal says
        // what is served rather than asking under a name nobody verified.
        await using LoopbackServer server = new(_ => new StubResponse(404, "no such dataset"));
        TestTradingRuntime tradingRuntime = new();
        Instrument krakenFuture = Instrument("KRAKEN", "PF_XBTUSD", InstrumentClass.Swap);
        tradingRuntime.TradingRuntime.Cache.AddInstrument(krakenFuture);

        TardisDataClient client = new(
            new ClientId("TARDIS"),
            new TardisDataClientConfig { ApiKey = "test-key", BaseUrl = server.HttpBase + "/v1" },
            tradingRuntime.Services);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LoadTradesAsync(krakenFuture.Id, null, null, null, CancellationToken.None));

        Assert.Contains("KRAKEN", refused.Message, StringComparison.Ordinal);
        Assert.Contains("KRAKEN:Spot", refused.Message, StringComparison.Ordinal);

        // And nothing was asked of the vendor: a guessed name is answered with silence, which reads as the vendor
        // being empty rather than as us being wrong.
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void Every_entry_names_a_market_this_engine_actually_ships()
    {
        // A dead entry is a promise nobody can use, and would hide a venue being renamed or dropped.
        string[] venues = [.. Repo.ShippedVenues().Select(v => v.ToUpperInvariant())];

        foreach (string key in new TardisDataClientConfig().ExchangeMap.Keys)
        {
            string venue = key.Split(':')[0];

            Assert.Contains(venue, venues, StringComparer.Ordinal);
            Assert.True(
                Enum.TryParse(key.Split(':')[1], out InstrumentClass _),
                $"'{key}' does not name an instrument class this engine has.");
        }
    }
}
