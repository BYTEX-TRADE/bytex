using System.Text.RegularExpressions;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests;

// Why (R4.12, the per-venue half): how much margin a venue requires, and the most leverage it will grant, are facts
// about an instrument that only the venue knows. Two of the three shipped adapters had invented them - 0.05 initial
// and 0.025 maintenance, the same pair for every contract - and nothing anywhere read a maximum leverage at all.
//
// Measured against the live public endpoints on 2026-09-25, which is what made the size of it clear:
//
//   Bybit   grants 150x on BTCUSDT and requires 0.0066 / 0.0033. The declared 0.05 was 7.6 times the truth.
//   KuCoin  publishes 0.008 / 0.004 and 125x on its contract, and the adapter already read the margin.
//   Binance publishes requiredMarginPercent and maintMarginPercent PER SYMBOL in the same response the instruments
//           come from. The hard-coded pair happened to equal BTCUSDT's and was a guess on the other 908.
//
// The error was not cosmetic, and this is the part worth remembering: Instrument.InitialMarginRate is
// Math.Max(1/leverage, MarginInit), so MarginInit is a FLOOR. A wrong 0.05 meant every leverage above 20x quietly
// cost the margin of 20x, on a venue granting 150x - a backtest sized against it liquidates at the wrong price, and
// the run completes and looks like a result.
//
// So the guard below is not "these three venues are right today". It reads the adapters' source and fails if any
// venue writes a margin figure of its own - which is what the five venues arriving next have to satisfy without
// anybody telling them the rule.
public sealed class VenueMarginTests
{
    /// <summary>
    /// A margin assignment whose value is a number somebody typed, rather than one read from the venue.
    /// <para>
    /// A literal that is the NUMERATOR OF A DIVISION is not one of those, which is what the lookahead is for. The
    /// rule this file states is that MarginInit must not be a floor over 1/leverage; a venue that publishes a
    /// maximum leverage per contract and writes <c>1m / maxLeverage</c> has written exactly that reciprocal and
    /// nothing else, and the only literal on the line is the 1 of "one over". Without the lookahead this read that
    /// as an invented figure and failed the venue that obeys the rule most directly of any here. Something still
    /// has to supply the denominator, so a typed pair - <c>5m / 100m</c> - is caught on the denominator instead.
    /// </para>
    /// </summary>
    private static readonly Regex _invented = new(
        @"Margin(Init|Maint)\s*=.*?(?<literal>\b(?:[1-9]\d*|\d+\.\d+)m\b)(?!\s*/)",
        RegexOptions.Compiled);

    [Fact]
    public void No_adapter_writes_down_the_margin_its_venue_requires()
    {
        // Zero is allowed and is not a guess: a cash or spot account borrows nothing, so its margin really is none.
        // Anything else typed into the assignment is a number the venue was never asked for.
        foreach (string venue in Repo.ShippedVenues())
        {
            foreach (string file in Repo.SourceFiles(venue))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    // A named constant IS allowed: a venue-wide default the venue itself publishes needs somewhere
                    // to live, and a name that says what it is satisfies the rule. What is forbidden is a number
                    // typed straight into the mapping, where nothing says where it came from.
                    if (lines[i].Contains(" const ", StringComparison.Ordinal)
                        || lines[i].Contains("static readonly", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Match match = _invented.Match(lines[i]);
                    Assert.False(
                        match.Success,
                        $"{Path.GetFileName(file)}:{i + 1} declares a margin of {match.Groups["literal"].Value} instead of "
                        + "reading what the venue requires. Every venue publishes it, and a figure typed here is a floor "
                        + "under InitialMarginRate that silently overcharges every leverage above its reciprocal.");
                }
            }
        }
    }

    [Fact]
    public void A_venue_wide_default_is_a_named_constant_and_not_a_number_in_a_mapping()
    {
        // The one legitimate place a figure may live: Binance publishes a venue-wide default that a symbol can
        // omit, so the adapter needs it - as something with a name that says what it is, behind the published
        // value rather than in front of it.
        Assert.Equal(0.05m, BinanceVenue.DefaultMarginInit);
        Assert.Equal(0.025m, BinanceVenue.DefaultMarginMaint);
        Assert.Equal(100m, BinanceVenue.MarginPercentToFraction);
    }

    // ----- and each venue reads its own -----

    [Fact]
    public async Task Binance_reads_the_margin_its_own_response_publishes_per_symbol()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo)
            .Handle);

        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures, BaseUrlHttp = server.HttpBase }, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.UsdMFutures, null, null);

        await provider.LoadAllAsync(CancellationToken.None);

        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BINANCE"))!;

        // The fixture carries this venue's own percentages, and the engine holds a fraction - a factor of a hundred
        // apart, which is the kind of mistake that produces a plausible number rather than a broken one.
        //
        // These happen to equal the pair the adapter used to hard-code, and that is the point rather than a
        // coincidence worth hiding: what this venue publishes publicly is a venue-wide DEFAULT and not the minimum
        // it will take. 5 percent supports at most 20x while the venue grants 125x, so it cannot be the bracket
        // minimum, and it remains a floor that clamps everything above 20x. Sourcing the number fixed where it came
        // from; the real brackets are behind a signed endpoint and are still owed.
        Assert.Equal(0.05m, perp.MarginInit);
        Assert.Equal(0.025m, perp.MarginMaint);

        // Null, and deliberately. This venue keeps its notional brackets behind a signed endpoint, so the ceiling
        // cannot be read from public data - and "not published" is a different answer from "unlimited" to anyone
        // deciding whether a configured leverage is reachable.
        Assert.Null(perp.MaxLeverage);
    }

    [Fact]
    public async Task Binances_coin_margined_family_reads_the_margin_from_its_own_response_as_well()
    {
        // The third family of the same venue, held to the same rule and measured in its own right: every one of its
        // 30 contracts publishes requiredMarginPercent and maintMarginPercent, and every one publishes the SAME
        // pair - 5.0000 and 2.5000, from the 100-USD BTCUSD perpetual to the 10-USD altcoin quarterlies. A figure
        // identical across every contract of a market is not what that market requires of each of them, which is
        // why it stays a venue-wide default behind the signed brackets rather than becoming the answer.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/dapi/v1/exchangeInfo", BinancePayloads.CoinMExchangeInfo)
            .Handle);

        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.CoinMFutures, BaseUrlHttp = server.HttpBase }, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.CoinMFutures, null, null);

        await provider.LoadAllAsync(CancellationToken.None);

        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;

        Assert.Equal(0.05m, perp.MarginInit);
        Assert.Equal(0.025m, perp.MarginMaint);

        // Null, and deliberately, exactly as on its sibling: an unauthenticated call to this family's own bracket
        // endpoint is refused with -2014, so the ceiling cannot be read from public data - and "not published" is a
        // different answer from "unlimited" to anything deciding whether a configured leverage is reachable.
        Assert.Null(perp.MaxLeverage);
    }

    [Fact]
    public async Task KuCoin_reads_the_margin_and_the_ceiling_from_the_contract()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts)
            .Handle);

        KucoinHttp http = new(new KucoinDataClientConfig { ProductType = KucoinProductType.Futures, BaseUrlHttp = server.HttpBase });
        KucoinFuturesInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Instrument perp = provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;

        // The only one of the three shipped venues that gives margin and ceiling in one public response, so reading
        // the ceiling cost nothing and it was simply never read.
        Assert.True(perp.MarginInit > 0m, "this venue publishes an initial margin and it must be read");
        Assert.True(perp.MarginMaint > 0m, "this venue publishes a maintenance margin and it must be read");
        Assert.NotNull(perp.MaxLeverage);
        Assert.True(perp.MaxLeverage > 1m, "a ceiling of one or less would mean this venue grants no leverage at all");
    }

    [Fact]
    public void A_maximum_leverage_survives_being_written_to_the_catalog_and_read_back()
    {
        // An instrument is persisted and reloaded, and a fact that does not survive that is a fact the engine has
        // only while it is online. Null has to survive as null too: it means the venue did not say, and a round trip
        // that turned it into zero would read as a venue granting no leverage.
        Instrument declared = BinanceExecRig.Perpetual();

        Assert.Null(declared.MaxLeverage);
        Assert.Null(Roundtrip(declared).MaxLeverage);

        Instrument capped = new CryptoPerpetual(new InstrumentSpec
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
            MaxLeverage = 150m,
        });

        Assert.Equal(150m, Roundtrip(capped).MaxLeverage);
        Assert.Equal(0.0066m, Roundtrip(capped).MarginInit);
    }

    private static Instrument Roundtrip(Instrument instrument) =>
        Bytex.Data.InstrumentJson.Deserialize(Bytex.Data.InstrumentJson.Serialize(instrument));
}
