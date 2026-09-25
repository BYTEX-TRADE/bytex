using System.Text.RegularExpressions;
using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: the risk engine is the last thing between a strategy and a venue, and every reason it can refuse an order is
// a rule somebody relies on. Counting them found two with no test at all - PRICE_INCREMENT and QUANTITY_INCREMENT,
// the guards that stop an order a venue would reject outright - and several with exactly one.
//
// Each rule gets an instrument shaped so that THAT rule is the one reached: a bound is only testable when the
// bounds in front of it let the order through, and writing them all against one instrument is how the increment
// rules stayed untested - price precision refused the order first every time.
//
// The last test reads the engine's own source for the reasons it can return and fails if one is never exercised, so
// a new guard cannot arrive untested.
public class RiskEngineDenialMatrixTests
{
    private static CurrencyPair Instrument(
        decimal priceIncrement = 0.01m,
        byte pricePrecision = 2,
        decimal sizeIncrement = 0.001m,
        byte sizePrecision = 3,
        Quantity? minQuantity = null,
        Quantity? maxQuantity = null,
        Price? minPrice = null,
        Price? maxPrice = null,
        Money? minNotional = null,
        Money? maxNotional = null) => new(new InstrumentSpec
        {
            Id = TestIds.BtcUsdt,
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(priceIncrement, pricePrecision),
            SizeIncrement = new Quantity(sizeIncrement, sizePrecision),
            MinQuantity = minQuantity,
            MaxQuantity = maxQuantity,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            MinNotional = minNotional,
            MaxNotional = maxNotional,
        });

    private static string DenialOf(CurrencyPair instrument, string quantity, string price, RiskEngineConfig? config = null, decimal balance = 100_000_000m)
    {
        RiskHarness h = new(config);
        h.Cache.AddInstrument(instrument);
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, balance, 0m), (Currencies.BTC, 1_000m, 0m))));

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, quantity, price));

        Assert.Empty(h.Forwarded);
        return Assert.Single(h.Denied).Reason;
    }

    [Fact]
    public void A_price_off_the_tick_is_refused()
    {
        // Untested before this, and it needed an instrument whose tick is coarser than its precision: at tick 0.50
        // with one decimal, 50000.3 has the right number of decimals and is still not a price this venue quotes.
        // Written against a 0.01 tick the precision rule refuses it first, which is why nobody had reached this one.
        string reason = DenialOf(Instrument(priceIncrement: 0.5m, pricePrecision: 1), "1.000", "50000.3");

        Assert.StartsWith("PRICE_INCREMENT", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quantity_off_the_size_step_is_refused()
    {
        // The same shape on the size: a step of 5 whole contracts, and 7 of them is not a size this venue accepts.
        // The rule most likely to be reached by a sizing mode that divides a balance by a price.
        string reason = DenialOf(Instrument(sizeIncrement: 5m, sizePrecision: 0), "7", "50000.00");

        Assert.StartsWith("QUANTITY_INCREMENT", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_price_below_the_instruments_minimum_is_refused()
    {
        string reason = DenialOf(Instrument(minPrice: new Price(100m, 2)), "1.000", "50.00");

        Assert.StartsWith("PRICE_LESS_THAN_MIN", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_price_above_the_instruments_maximum_is_refused()
    {
        string reason = DenialOf(Instrument(maxPrice: new Price(60_000m, 2)), "1.000", "70000.00");

        Assert.StartsWith("PRICE_EXCEEDS_MAX", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quantity_below_the_instruments_minimum_is_refused()
    {
        // Above zero, on the step, and still under the floor - which is the only way to reach this rule, because a
        // zero quantity is refused by the order itself before any engine sees it.
        string reason = DenialOf(Instrument(minQuantity: new Quantity(1m, 3)), "0.500", "50000.00");

        Assert.StartsWith("QUANTITY_LESS_THAN_MIN", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quantity_above_the_instruments_maximum_is_refused()
    {
        string reason = DenialOf(Instrument(maxQuantity: new Quantity(10m, 3)), "20.000", "50000.00");

        Assert.StartsWith("QUANTITY_EXCEEDS_MAX", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notional_below_the_instruments_minimum_is_refused()
    {
        // Legal price, legal size, and the two multiplied are still too small to be worth a venue's time.
        string reason = DenialOf(Instrument(minNotional: new Money(1_000m, Currencies.USDT)), "0.001", "50000.00");

        Assert.StartsWith("NOTIONAL_LESS_THAN_MIN", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notional_above_the_instruments_maximum_is_refused()
    {
        string reason = DenialOf(Instrument(maxNotional: new Money(10_000m, Currencies.USDT)), "1.000", "50000.00");

        Assert.StartsWith("NOTIONAL_EXCEEDS_MAX", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notional_above_the_per_order_cap_is_refused()
    {
        // The option that could not be loaded from a file at all until the serializer learned to key a dictionary by
        // an instrument. Worth one test that it still does what it says once it loads.
        string reason = DenialOf(
            Instrument(),
            "1.000",
            "50000.00",
            new RiskEngineConfig { MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [TestIds.BtcUsdt] = 1_000m } });

        Assert.StartsWith("NOTIONAL_EXCEEDS_MAX_PER_ORDER", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_price_with_more_decimals_than_the_instrument_quotes_is_refused()
    {
        string reason = DenialOf(Instrument(), "1.000", "50000.123");

        Assert.StartsWith("PRICE_PRECISION", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quantity_with_more_decimals_than_the_instrument_trades_is_refused()
    {
        string reason = DenialOf(Instrument(), "1.0001", "50000.00");

        Assert.StartsWith("QUANTITY_PRECISION", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_order_the_cash_balance_cannot_cover_is_refused()
    {
        string reason = DenialOf(Instrument(), "1.000", "50000.00", balance: 100m);

        Assert.StartsWith("INSUFFICIENT_BALANCE", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_reason_the_engine_can_give_is_exercised_by_a_test()
    {
        // The guard on the guards: it reads the engine's source for the codes it can return and checks each appears
        // in some test, so a new rule cannot ship with nobody having tried it. Counting these by hand is what found
        // PRICE_INCREMENT and QUANTITY_INCREMENT untested, and doing it by hand is not a plan.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Bytex.Core", "Engines", "RiskEngine.cs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string engine = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Bytex.Core", "Engines", "RiskEngine.cs"));
        string[] codes = Regex.Matches(engine, "\"(?<code>[A-Z][A-Z_]{3,}):")
            .Select(m => m.Groups["code"].Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(codes);

        string tests = string.Join(
            "\n",
            new DirectoryInfo(Path.Combine(dir.FullName, "tests"))
                .GetFiles("*.cs", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(f => File.ReadAllText(f.FullName)));

        string[] untested = codes.Where(c => !tests.Contains(c, StringComparison.Ordinal)).ToArray();
        Assert.True(
            untested.Length == 0,
            $"the risk engine can refuse an order for reasons no test exercises: {string.Join(", ", untested)}. "
            + "Every reason it can give is a rule somebody relies on, so each needs a test that reaches it.");
    }
}
