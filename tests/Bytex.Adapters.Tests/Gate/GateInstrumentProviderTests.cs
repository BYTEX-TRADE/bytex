using Bytex.Adapters.Gate;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Gate;

// Why: the three providers read three different payloads out of one venue, and every number they publish is one a
// strategy sizes a position with. Three of those numbers are published in a form no other venue in this repository
// uses - a precision as a COUNT of places, a fee as a PERCENTAGE, a size as a number of CONTRACTS - and reading any
// of them as the usual form gives a plausible wrong answer rather than an error.
public sealed class GateInstrumentProviderTests
{
    private static async Task<GateInstrumentProvider> SpotAsync(Routes routes, LoopbackServer server)
    {
        server.Handler = routes.Handle;
        GateInstrumentProvider provider = new(new GateHttp(new GateDataClientConfig { BaseUrlHttp = server.HttpBase }));
        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    // ----- spot -----

    [Fact]
    public async Task A_spot_precision_is_a_count_of_places_and_becomes_an_increment()
    {
        // BTC_USDT publishes precision 1 and amount_precision 6, which means a tick of 0.1 and a step of 0.000001.
        // Read as increments they would be a tick of one whole quote unit and a step of six BTC.
        await using LoopbackServer server = new();
        GateInstrumentProvider provider = await SpotAsync(
            new Routes().On("GET", "/api/v4/spot/currency_pairs", GatePayloads.CurrencyPairs),
            server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC_USDT.GATE"))!;

        Assert.Equal(0.1m, btc.PriceIncrement.Value);
        Assert.Equal(1, btc.PricePrecision);
        Assert.Equal(0.000001m, btc.SizeIncrement.Value);
        Assert.Equal(6, btc.SizePrecision);

        Instrument eth = provider.Find(InstrumentId.Parse("ETH_USDT.GATE"))!;
        Assert.Equal(0.01m, eth.PriceIncrement.Value);
        Assert.Equal(0.0001m, eth.SizeIncrement.Value);
    }

    [Fact]
    public async Task A_spot_fee_is_a_percentage_and_becomes_a_fraction()
    {
        // The venue says "0.2" and means twenty basis points. Carried as written, every backtest on this venue would
        // pay a hundred times the real commission - which reads as a strategy that does not work.
        await using LoopbackServer server = new();
        GateInstrumentProvider provider = await SpotAsync(
            new Routes().On("GET", "/api/v4/spot/currency_pairs", GatePayloads.CurrencyPairs),
            server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC_USDT.GATE"))!;

        Assert.Equal(0.002m, btc.MakerFee);
        Assert.Equal(0.002m, btc.TakerFee);
        Assert.Equal(100m, GateVenue.SpotFeePercentToFraction);
    }

    [Fact]
    public async Task A_spot_pair_the_venue_will_not_take_both_sides_of_is_left_out()
    {
        // The venue has three trade statuses and only one of them takes an order on both sides. Publishing a
        // sell-only pair as tradable would have a strategy's buy refused by the venue with nothing above the
        // adapter able to explain it.
        await using LoopbackServer server = new();
        GateInstrumentProvider provider = await SpotAsync(
            new Routes().On("GET", "/api/v4/spot/currency_pairs", GatePayloads.CurrencyPairs),
            server);

        Assert.Equal(2, provider.Count);
        Assert.Null(provider.Find(InstrumentId.Parse("POOLX_USDT.GATE")));
        Assert.Null(provider.Find(InstrumentId.Parse("MAG7XON_USDT.GATE")));
        Assert.Equal(GateVenue.SpotTradable, "tradable");
    }

    [Fact]
    public async Task A_spot_pair_carries_the_quote_limits_the_venue_publishes()
    {
        await using LoopbackServer server = new();
        GateInstrumentProvider provider = await SpotAsync(
            new Routes().On("GET", "/api/v4/spot/currency_pairs", GatePayloads.CurrencyPairs),
            server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC_USDT.GATE"))!;

        Assert.Equal(InstrumentClass.Spot, btc.InstrumentClass);
        Assert.Equal("BTC", btc.BaseCurrency!.Code);
        Assert.Equal("USDT", btc.QuoteCurrency.Code);
        Assert.Equal(0.000001m, btc.MinQuantity!.Value);
        Assert.Equal(100m, btc.MaxQuantity!.Value);
        Assert.Equal(3m, btc.MinNotional!.Value.Amount);
        Assert.Equal(5_000_000m, btc.MaxNotional!.Value.Amount);
    }

    [Fact]
    public async Task One_spot_pair_can_be_loaded_by_name()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/spot/currency_pairs/BTC_USDT", GatePayloads.CurrencyPair)
            .Handle);

        GateInstrumentProvider provider = new(new GateHttp(new GateDataClientConfig { BaseUrlHttp = server.HttpBase }));
        await provider.LoadAsync(InstrumentId.Parse("BTC_USDT.GATE"), CancellationToken.None);

        Instrument only = Assert.Single(provider.GetAll());
        Assert.Equal("BTC_USDT", only.RawSymbol!.Value);
    }

    // ----- perpetual futures -----

    private static async Task<GateFuturesInstrumentProvider> FuturesAsync(LoopbackServer server, string payload = GatePayloads.FuturesContracts)
    {
        server.Handler = new Routes().On("GET", "/api/v4/futures/usdt/contracts", payload).Handle;
        GateFuturesInstrumentProvider provider = new(
            new GateHttp(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase }));

        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    [Fact]
    public async Task A_contracts_size_increment_is_its_multiplier_in_base_currency()
    {
        // The conversion that makes a strategy portable. One BTC_USDT contract is 0.0001 BTC, so the smallest
        // tradable quantity - and the step between tradable quantities - is 0.0001 BTC, published exactly as
        // Binance and Bybit publish theirs. The venue's own contract size goes in Info for the adapter to read.
        await using LoopbackServer server = new();
        GateFuturesInstrumentProvider provider = await FuturesAsync(server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC_USDT.GATE"))!;

        Assert.Equal(0.0001m, btc.SizeIncrement.Value);
        Assert.Equal(0.0001m, btc.MinQuantity!.Value);
        Assert.Equal(0.0001m, GateFuturesVenue.Multiplier(btc));
        Assert.Equal("0.0001", btc.Info![GateFuturesVenue.MultiplierInfo]);

        // And the notional multiplier is one, because a quantity is already in base currency by the time anything
        // above the adapter sees it - the same as on the other venues.
        Assert.Equal(1m, btc.Multiplier!.Value);
    }

    [Fact]
    public async Task A_contracts_maximum_size_is_converted_out_of_contracts_too()
    {
        // 12,000,000 contracts of 0.0001 BTC is 1,200 BTC. Left in contracts it would read as a maximum order of
        // twelve million BTC, which is more than exists.
        await using LoopbackServer server = new();
        GateFuturesInstrumentProvider provider = await FuturesAsync(server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC_USDT.GATE"))!;
        Assert.Equal(1200m, btc.MaxQuantity!.Value);
    }

    [Fact]
    public async Task A_contract_that_takes_fractional_sizes_still_has_a_minimum_of_one_contract()
    {
        // ETH_USDT and ARIA_USDT are two of the fourteen contracts the venue has enabled fractional sizes on, and it
        // publishes order_size_min as ZERO for them. Zero is not a size anything can send, so the increment stands
        // in - a MinQuantity of nothing would let a strategy submit an order the venue refuses.
        await using LoopbackServer server = new();
        GateFuturesInstrumentProvider provider = await FuturesAsync(server);

        Instrument aria = provider.Find(InstrumentId.Parse("ARIA_USDT.GATE"))!;
        Assert.Equal(100m, aria.SizeIncrement.Value);
        Assert.Equal(100m, aria.MinQuantity!.Value);
        Assert.True(aria.MinQuantity.Value > 0m);
    }

    [Fact]
    public async Task A_contracts_margin_comes_from_the_venue_rather_than_from_a_constant()
    {
        // BTC_USDT publishes leverage_max 200 and maintenance_rate 0.003, and ARIA_USDT 10 and 0.08. Two contracts
        // on one market with initial margin rates that differ by a factor of twenty, which is why a hard-coded
        // figure for every contract - as two older adapters here carry - cannot be right for both.
        await using LoopbackServer server = new();
        GateFuturesInstrumentProvider provider = await FuturesAsync(server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC_USDT.GATE"))!;
        Assert.Equal(0.005m, btc.MarginInit);
        Assert.Equal(0.003m, btc.MarginMaint);

        Instrument aria = provider.Find(InstrumentId.Parse("ARIA_USDT.GATE"))!;
        Assert.Equal(0.1m, aria.MarginInit);
        Assert.Equal(0.08m, aria.MarginMaint);

        Instrument aapl = provider.Find(InstrumentId.Parse("AAPL_USDT.GATE"))!;
        Assert.Equal(0.01m, aapl.MarginInit);
        Assert.Equal(0.005m, aapl.MarginMaint);
    }

    [Fact]
    public async Task A_contract_carries_the_makers_rebate_as_the_venue_states_it()
    {
        // The maker rate is NEGATIVE on this whole family - the venue pays a rebate - and the instrument carries it
        // as published. It is the family DECLARATION that cannot: a declared fee is held to a range starting at
        // zero, so the declaration reads as nothing paid and the instrument is the truth.
        await using LoopbackServer server = new();
        GateFuturesInstrumentProvider provider = await FuturesAsync(server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC_USDT.GATE"))!;

        Assert.Equal(-0.0001m, btc.MakerFee);
        Assert.Equal(0.00075m, btc.TakerFee);
        Assert.True(btc.MakerFee < 0m);
    }

    [Fact]
    public async Task An_inverse_contract_is_left_out_because_its_quantity_has_no_base_form()
    {
        // The venue's BTC-settled market holds one contract and it is inverse: quoted in USD, settled in BTC, with a
        // quanto_multiplier of zero. A quantity of one cannot be expressed in base units without a price, so it is
        // not published - the same rule KuCoin's adapter applies to its six inverse contracts.
        await using LoopbackServer server = new();
        GateFuturesInstrumentProvider provider = await FuturesAsync(server, GatePayloads.InverseContracts);

        Assert.Empty(provider.GetAll());
        Assert.Equal("direct", GateFuturesVenue.LinearType);
    }

    [Fact]
    public async Task Every_contract_of_this_market_is_a_perpetual_swap()
    {
        // The class comes from the endpoint and from the venue's own fields, never from the name - which matters
        // here because AAPL_USDT is a perpetual contract on a tokenised equity and its name says nothing about it.
        await using LoopbackServer server = new();
        GateFuturesInstrumentProvider provider = await FuturesAsync(server);

        Assert.All(provider.GetAll(), i => Assert.Equal(InstrumentClass.Swap, i.InstrumentClass));
        Assert.All(provider.GetAll(), i => Assert.IsType<CryptoPerpetual>(i));
        Assert.Equal(4, provider.Count);
    }

    // ----- delivery -----

    private static async Task<GateDeliveryInstrumentProvider> DeliveryAsync(LoopbackServer server)
    {
        server.Handler = new Routes().On("GET", "/api/v4/delivery/usdt/contracts", GatePayloads.DeliveryContracts).Handle;
        GateDeliveryInstrumentProvider provider = new(
            new GateHttp(new GateDataClientConfig { ProductType = GateProductType.Delivery, BaseUrlHttp = server.HttpBase }));

        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    [Fact]
    public async Task A_dated_contract_is_a_future_with_the_expiry_the_venue_publishes()
    {
        // expire_time is in SECONDS, as every timestamp on this venue is. Read as milliseconds it would put the
        // expiry three weeks after the epoch and make every dated contract look long since settled.
        await using LoopbackServer server = new();
        GateDeliveryInstrumentProvider provider = await DeliveryAsync(server);

        CryptoFuture btc = Assert.IsType<CryptoFuture>(provider.Find(InstrumentId.Parse("BTC_USDT_20261009.GATE")));

        Assert.Equal(InstrumentClass.Future, btc.InstrumentClass);
        Assert.Equal(UnixNanos.FromSeconds(1791532800), btc.Expiration);
        // Eight in the morning UTC on the day the contract's name gives, which is the venue's settlement hour - and
        // a reason not to read an expiry off a name: the name says 20261009 and the contract settles part way
        // through that day.
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero), btc.Expiration.ToDateTimeOffset());
    }

    [Fact]
    public async Task A_dated_contracts_pair_comes_from_the_venues_own_underlying_field()
    {
        // The contract is named BTC_USDT_20261009 and the venue says separately that it tracks BTC_USDT. The pair is
        // read from that field rather than from the name, because the name carries a date the pair does not.
        await using LoopbackServer server = new();
        GateDeliveryInstrumentProvider provider = await DeliveryAsync(server);

        Instrument eth = provider.Find(InstrumentId.Parse("ETH_USDT_20261009.GATE"))!;

        Assert.Equal("ETH", eth.BaseCurrency!.Code);
        Assert.Equal("USDT", eth.QuoteCurrency.Code);
        Assert.Equal("USDT", eth.SettlementCurrency.Code);

        // And the raw symbol keeps the date, because that is what the venue is asked about.
        Assert.Equal("ETH_USDT_20261009", eth.RawSymbol!.Value);
    }

    [Fact]
    public async Task A_dated_contract_is_sized_in_contracts_like_a_perpetual_one()
    {
        await using LoopbackServer server = new();
        GateDeliveryInstrumentProvider provider = await DeliveryAsync(server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC_USDT_20261009.GATE"))!;

        Assert.Equal(0.0001m, btc.SizeIncrement.Value);
        Assert.Equal(0.0001m, GateFuturesVenue.Multiplier(btc));
        Assert.Equal(0.01m, GateFuturesVenue.Multiplier(provider.Find(InstrumentId.Parse("ETH_USDT_20261009.GATE"))!));

        // And its margin comes off the venue exactly as a perpetual's does.
        Assert.Equal(0.01m, btc.MarginInit);
        Assert.Equal(0.005m, btc.MarginMaint);
    }

    [Fact]
    public async Task A_dated_contract_records_the_cycle_the_venue_names_and_no_activation_it_does_not()
    {
        // The venue publishes a settlement cycle and publishes NO creation, launch or listing time for a dated
        // contract, so the activation reported is the absence rather than a date nobody published.
        await using LoopbackServer server = new();
        GateDeliveryInstrumentProvider provider = await DeliveryAsync(server);

        CryptoFuture btc = Assert.IsType<CryptoFuture>(provider.Find(InstrumentId.Parse("BTC_USDT_20261009.GATE")));

        Assert.Equal("BI-WEEKLY", btc.Info![GateFuturesVenue.CycleInfo]);
        Assert.Equal(default, btc.Activation);
    }
}
