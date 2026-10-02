using Bytex.Adapters.Binance;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests;

// Why: an adapter turns numbers into text and puts that text on the wire. A venue reading "0,5" where the engine meant
// "0.5" rejects the order if it is lucky and fills the wrong size if it is not, and the machine that produced it would
// be the only one where it happens.
//
// The prices, quantities and amounts in a request are Price, Quantity and Money, which format themselves invariantly,
// so the wire is safe by construction - this pins that rather than trusting it. The messages below are the other half:
// bare decimals that a person compares against a venue's own documentation.
public sealed class CultureTests
{
    [Fact]
    public async Task An_order_reaches_the_venue_with_the_same_numbers_under_any_culture()
    {
        string invariant = await SubmittedQueryAsync();

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, await SubmittedQueryAsync());
        }

        // The numbers really are in it, written the way the venue documents them.
        Assert.Contains("quantity=0.5", invariant, StringComparison.Ordinal);
        Assert.Contains("price=25000.1", invariant, StringComparison.Ordinal);
    }

    private static async Task<string> SubmittedQueryAsync()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        rig.Routes.On("POST", "/api/v3/order", """{"symbol":"BTCUSDT","orderId":28,"orderListId":-1,"clientOrderId":"x","transactTime":1507725176595}""");

        await rig.SubmitAsync(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m)));

        RecordedRequest request = Assert.Single(rig.Server.Requests);

        // Without the signature and the timestamp, which are of the moment rather than of the culture.
        return string.Join(
            "&",
            request.Path,
            string.Join("&", request.QueryPairs
                .Where(p => p.Key is not ("signature" or "timestamp" or "newClientOrderId"))
                .Select(p => p.Key + "=" + p.Value)
                .Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void A_venue_that_refuses_a_size_names_it_the_same_under_any_culture()
    {
        // Gate and KuCoin trade whole contracts, and the refusal tells somebody what their size came to in them. The
        // three numbers in it are bare decimals.
        Instrument gate = new CryptoPerpetual(new InstrumentSpec
        {
            Id = MarketKey.Parse("bx-market:v2/GATE/BTC_USDT"),
            RawSymbol = new Symbol("BTC_USDT"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = Currency.FromCode("USDT", 8),
            BaseCurrency = Currency.FromCode("BTC", 8),
            SettlementCurrency = Currency.FromCode("USDT", 8),
            PricePrecision = 1,
            SizePrecision = 8,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.0001m, 8),
            Info = new Dictionary<string, string>(StringComparer.Ordinal) { ["contractMultiplier"] = "0.0001" },
        });
        Quantity awkward = new(0.00015m, 8);

        string invariant = Assert.Throws<ArgumentException>(() => GateFuturesVenue.ToContracts(gate, awkward)).Message;

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, Assert.Throws<ArgumentException>(() => GateFuturesVenue.ToContracts(gate, awkward)).Message);
        }

        Assert.Contains("0.00015", invariant, StringComparison.Ordinal);
        Assert.Contains("1.5 contracts", invariant, StringComparison.Ordinal);
    }

    [Fact]
    public void A_venue_that_refuses_a_leverage_names_it_the_same_under_any_culture()
    {
        // Two venues take whole numbers only, and both name the fraction they were given back to the reader. The same
        // sentence on a comma machine would read "5,5", which is not what anybody configured and not what the venue's
        // own documentation calls it.
        using TestTradingRuntime tradingRuntime = new();

        string invariant = Refusal(tradingRuntime);

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, Refusal(tradingRuntime));
        }

        Assert.Contains("configured for 5.5.", invariant, StringComparison.Ordinal);
    }

    private static string Refusal(TestTradingRuntime tradingRuntime) => Assert.Throws<ArgumentException>(() => new HyperliquidExecutionClient(
        new ClientId("HYPERLIQUID"),
        new HyperliquidExecutionClientConfig
        {
            PrivateKey = "0x1111111111111111111111111111111111111111111111111111111111111112",
            Leverage = 5.5m,
        },
        tradingRuntime.Services)).Message;
}
