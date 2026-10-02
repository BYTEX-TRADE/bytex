using Bytex.Adapters.Kraken;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests;

// Why: an adapter read a venue's declared decimal places and cast them to a byte. Kraken's futures market publishes
// that field SIGNED, and serves negative values on contracts that trade in multiples larger than one - a step of a
// thousand is declared as -3. Cast to a byte that is 253, and Quantity refuses any precision above 18, so the
// exception escaped the parse, escaped LoadAllAsync, and left the provider empty.
//
// The consequence was not one odd contract being skipped. **No Kraken perpetual or dated future could be listed,
// added or traded at all**, while Kraken spot worked perfectly - so the venue looked half-alive rather than broken.
//
// Nothing caught it on either side, and both reasons are worth keeping: the recorded payloads carried only 0 and 4,
// because they were written from the venue's documentation rather than from the contracts it serves; and the host's
// venue check walked each venue's FIRST market, which on Kraken is spot. Two green checks over a family neither had
// touched.
public sealed class VenuePrecisionTests
{
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(2, 2, 0.01)]
    [InlineData(8, 8, 0.00000001)]
    [InlineData(-1, 0, 10)]
    [InlineData(-3, 0, 1000)]
    public void Declared_places_become_a_precision_and_the_step_they_describe(long declared, byte precision, decimal increment)
    {
        // Negative places are not a smaller step, they are a LARGER one: there are no decimal places to keep, and the
        // contract trades in multiples. That is the case the cast turned into 253.
        Assert.Equal((precision, increment), VenuePrecision.FromDeclaredPlaces(declared));
    }

    [Fact]
    public void More_places_than_a_decimal_can_carry_are_held_rather_than_refused()
    {
        // Past eighteen a Price or Quantity throws, and throwing here would cost a whole market its instruments over
        // one strange contract - which is the failure this exists to prevent, and worse than a step finer than any
        // venue's real tick.
        Assert.Equal((VenuePrecision.Max, 0.000000000000000001m), VenuePrecision.FromDeclaredPlaces(40));
        Assert.Equal(18, VenuePrecision.Max);
    }

    [Fact]
    public async Task Krakens_futures_family_loads_the_contract_that_used_to_take_it_down()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/derivatives/api/v3/instruments", KrakenPayloads.Instruments)
            .On("GET", "/derivatives/api/v3/feeschedules", KrakenPayloads.FeeSchedules)
            .Handle);

        KrakenFuturesInstrumentProvider provider = new(
            new KrakenHttp(new KrakenDataClientConfig { ProductType = KrakenProductType.Futures, BaseUrlHttp = server.HttpBase }));

        await provider.LoadAllAsync(CancellationToken.None);

        // The whole family, not merely "no exception": the point of the defect was that ONE contract emptied the list.
        Assert.Equal(4, provider.GetAll().Count);

        Instrument thousands = provider.Find(MarketKey.Parse("bx-market:v2/KRAKEN/PF_SHIBUSD"))!;

        Assert.NotNull(thousands);
        Assert.Equal(0, thousands.SizePrecision);
        Assert.Equal(1000m, thousands.SizeIncrement.Value);
    }

    [Fact]
    public void No_adapter_casts_a_venues_declared_places_straight_to_a_byte()
    {
        // The guard, because the fix is only as good as the next adapter. Seven sites across three adapters did this,
        // and six of them were waiting for a venue to publish the value Kraken already publishes.
        System.Text.RegularExpressions.Regex cast = new(
            @"\(byte\)\s*\w+\.(Long|Int)\(",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (string venue in Repo.ShippedVenues())
        {
            foreach (string file in Repo.SourceFiles(venue))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    Assert.False(
                        cast.IsMatch(lines[i]),
                        $"{Path.GetFileName(file)}:{i + 1} casts a number the VENUE chose straight to a byte. A venue that "
                        + "publishes a negative or oversized precision then throws inside the parse, which empties the "
                        + "whole instrument list rather than skipping one contract. Read it through "
                        + $"{nameof(VenuePrecision)}.{nameof(VenuePrecision.FromDeclaredPlaces)} instead.");
                }
            }
        }
    }
}
