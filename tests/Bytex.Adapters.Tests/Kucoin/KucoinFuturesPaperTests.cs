using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Bytex.Live.Sandbox;

namespace Bytex.Adapters.Tests.Kucoin;

// Why: I told a host to refuse KuCoin perpetuals for BOTH paper and live, on the grounds that the futures execution
// client is not written. Live is right - there is nowhere to send an order. Paper was a guess, and it was wrong.
//
// A paper node does not use the venue's execution client at all. It uses the SANDBOX one, which matches orders
// against the market data flowing through the kernel. Everything it needs, the futures family already has: its
// instruments are published by its own data client, and its book, quotes and bars stream from the same place. So
// this is the test rather than the assertion - a paper venue named KUCOIN, holding a contract loaded by the real
// futures provider, filling an order at the price the futures quote implies, in base currency.
//
// It matters because "data first, execution later" will be the state of every venue onboarded from here, and a
// declaration that collapses paper and live into one flag would refuse something that works on all of them.
public sealed class KucoinFuturesPaperTests
{
    private static readonly UnixNanos _t0 = UnixNanos.FromSeconds(1_700_000_000);

    private static async Task<Instrument> LoadContractAsync(LoopbackServer server)
    {
        KucoinFuturesInstrumentProvider provider = new(new KucoinHttp(new KucoinDataClientConfig
        {
            ProductType = KucoinProductType.Futures,
            BaseUrlHttp = server.HttpBase,
        }));

        await provider.LoadAllAsync(CancellationToken.None);
        return provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;
    }

    [Fact]
    public async Task A_paper_node_fills_a_perpetual_even_though_the_venue_has_no_execution_client()
    {
        await using LoopbackServer venue = new(new Routes()
            .On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts)
            .Handle);

        // The contract comes out of the real futures provider, so its size increment is the venue's own contract
        // size expressed in base currency - 0.001 XBT - not something written by hand for this test.
        Instrument contract = await LoadContractAsync(venue);
        Assert.Equal(new Quantity(0.001m, 3), contract.SizeIncrement);

        TestClock clock = new(_t0);
        using Kernel kernel = new(new KernelConfig { Environment = TradingEnvironment.Sandbox }, clock);
        kernel.Cache.AddInstrument(contract);

        // A paper venue named after the venue whose data it is matching against. No KuCoin execution client exists
        // and none is registered: this is the sandbox one.
        SandboxExecutionClient paper = new(
            new ClientId("KUCOIN-PAPER"),
            new SandboxExecutionClientConfig
            {
                Venue = "KUCOIN",
                AccountType = AccountType.Margin,
                StartingBalances = ["100000 USDT"],
            },
            kernel.Services);

        kernel.ExecutionEngine.RegisterClient(paper);
        StrategyId strategy = new("PAPER-001");
        kernel.Start();
        await paper.ConnectAsync(CancellationToken.None);

        // It knows the contract, and says what it would match it against.
        Assert.Contains(contract.Id, paper.Matching().Select(m => m.InstrumentId));

        // A futures quote, through the same data engine a live node feeds.
        UnixNanos ts = _t0.Add(TimeSpan.FromSeconds(1));
        clock.SetTime(ts);
        kernel.DataEngine.OnData(new QuoteTick(
            contract.Id,
            contract.MakePrice(84_116.9m),
            contract.MakePrice(84_117m),
            contract.MakeQuantity(1m),
            contract.MakeQuantity(1m),
            ts,
            ts));

        OrderFactory orders = new(kernel.Services.TraderId, strategy, clock);
        MarketOrder order = orders.Market(contract.Id, OrderSide.Buy, contract.MakeQuantity(0.084m));
        kernel.Cache.AddOrder(order, null);

        await paper.SubmitOrderAsync(
            new SubmitOrder(kernel.Services.TraderId, strategy, order, null, null, null, Guid.NewGuid(), ts),
            CancellationToken.None);

        // Filled, at the ask the futures quote carried, for the size that was asked for - in base currency, the
        // same units a strategy sizes in on Binance and Bybit. 84 contracts of 0.001 XBT, and nothing above the
        // adapter ever said the word contract.
        OrderFilled fill = Assert.Single(kernel.Cache.Orders().SelectMany(o => o.Events).OfType<OrderFilled>());
        Assert.Equal(contract.Id, fill.InstrumentId);
        Assert.Equal(new Quantity(0.084m, 3), fill.LastQty);
        Assert.Equal(contract.MakePrice(84_117m), fill.LastPx);
    }
}
