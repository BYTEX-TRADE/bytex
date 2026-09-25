using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Examples.Strategies;
using Microsoft.Extensions.Logging;

namespace Bytex.Examples;

internal static class Program
{
    private static int Main(string[] args)
    {
        string example = args.Length > 0 ? args[0] : "backtest-ema-cross";
        switch (example)
        {
            case "backtest-ema-cross":
                return BacktestEmaCross(args.Skip(1).ToArray());
            case "write-sample-catalog":
                return WriteSampleCatalog(args.Skip(1).ToArray());
            default:
                Console.WriteLine("Unknown example. Available: backtest-ema-cross, write-sample-catalog <path> [bars]");
                return 1;
        }
    }

    /// <summary>
    /// Writes the sample instrument and synthetic minute bars into a catalog so the CLI can run a backtest from configuration.
    /// </summary>
    private static int WriteSampleCatalog(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "catalog";
        int bars = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 5_000;
        Instrument instrument = TestData.BtcUsdt();
        BarType barType = new(instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));
        Bytex.Data.ParquetDataCatalog catalog = new(path);
        catalog.WriteInstrumentsAsync([instrument]).GetAwaiter().GetResult();
        catalog.WriteBarsAsync(TestData.RandomWalkBars(instrument, barType, bars)).GetAwaiter().GetResult();
        Console.WriteLine($"Wrote {instrument.Id} and {bars} bars of {barType} to {catalog.RootPath}");
        return 0;
    }

    private static int BacktestEmaCross(string[] args)
    {
        int bars = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 5_000;
        using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddSimpleConsole(o => o.SingleLine = true));

        Instrument instrument = TestData.BtcUsdt();
        BarType barType = new(instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

        using BacktestEngine engine = new(new BacktestEngineConfig(), loggerFactory);
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = TestData.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
        });
        engine.AddData(TestData.RandomWalkBars(instrument, barType, bars).Cast<IData>());
        engine.AddStrategy(new EmaCross(new EmaCrossConfig
        {
            StrategyId = new StrategyId("EmaCross-001"),
            InstrumentId = instrument.Id,
            BarType = barType,
            FastPeriod = 10,
            SlowPeriod = 30,
            TradeSize = 0.5m,
        }));

        engine.Run();
        BacktestResult result = engine.GetResult();
        Console.WriteLine();
        Console.WriteLine(result.Summary());
        return 0;
    }
}
