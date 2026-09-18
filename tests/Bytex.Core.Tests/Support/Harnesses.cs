using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Engines;
using Bytex.Core.Kernel;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging.Abstractions;
using CoreKernel = Bytex.Core.Kernel.Kernel;

namespace Bytex.Core.Tests.Support;

/// <summary>
/// A risk engine wired to a real bus, cache, portfolio and test clock. The two execution-engine endpoints are
/// replaced by recorders, so a test sees exactly what the risk engine forwarded and what it denied.
/// </summary>
internal sealed class RiskHarness
{
    public RiskHarness(RiskEngineConfig? config = null, UnixNanos? now = null)
    {
        Clock = new TestClock(now ?? TestOrders.T0);
        Bus = new MessageBus(TestIds.Trader);
        Cache = new Cache();
        Portfolio = new Portfolio(Cache, Bus);
        Engine = new RiskEngine(Bus, Cache, Portfolio, config);
        Engine.Initialize(Clock, NullLoggerFactory.Instance);
        Bus.Register(Endpoints.ExecutionEngineExecute, m => Forwarded.Add((TradingCommand)m));
        Bus.Register(Endpoints.ExecutionEngineProcess, m => Events.Add((OrderEvent)m));
    }

    public TestClock Clock { get; }

    public MessageBus Bus { get; }

    public Cache Cache { get; }

    public Portfolio Portfolio { get; }

    public RiskEngine Engine { get; }

    public List<TradingCommand> Forwarded { get; } = new();

    public List<OrderEvent> Events { get; } = new();

    public IReadOnlyList<OrderDenied> Denied => Events.OfType<OrderDenied>().ToList();

    public IReadOnlyList<OrderModifyRejected> ModifyRejected => Events.OfType<OrderModifyRejected>().ToList();

    public SubmitOrder Submit(Order order)
    {
        SubmitOrder command = new(order.TraderId, order.StrategyId, order, null, null, null, Guid.NewGuid(), Clock.Timestamp);
        Bus.Send(Endpoints.RiskEngineExecute, command);
        return command;
    }

    public SubmitOrderList SubmitList(params Order[] orders)
    {
        OrderList list = new(new OrderListId("OL-1"), orders);
        SubmitOrderList command = new(TestIds.Trader, list.StrategyId, list, null, null, null, Guid.NewGuid(), Clock.Timestamp);
        Bus.Send(Endpoints.RiskEngineExecute, command);
        return command;
    }

    public ModifyOrder Modify(Order order, string? quantity = null, string? price = null, string? trigger = null)
    {
        ModifyOrder command = new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId,
            quantity is null ? null : Quantity.Parse(quantity), price is null ? null : Price.Parse(price), trigger is null ? null : Price.Parse(trigger),
            null, Guid.NewGuid(), Clock.Timestamp);
        Bus.Send(Endpoints.RiskEngineExecute, command);
        return command;
    }

    /// <summary>Puts an open position into the cache by applying a single fill.</summary>
    public Position AddPosition(Instrument instrument, OrderSide side, string quantity, string price, string positionId = "P-1")
    {
        MarketOrder opening = TestOrders.Market("O-OPEN-" + positionId, instrument.Id, side, quantity);
        Position position = new(instrument, TestEvents.Filled(opening, "T-OPEN-" + positionId, quantity, price, positionId: new PositionId(positionId)));
        Cache.AddPosition(position);
        return position;
    }

    /// <summary>Adds an order to the cache and walks it to Accepted so that it can be modified.</summary>
    public T AddAccepted<T>(T order) where T : Order
    {
        Cache.AddOrder(order);
        order.Apply(TestEvents.Submitted(order));
        order.Apply(TestEvents.Accepted(order, "V-" + order.ClientOrderId.Value));
        Cache.UpdateOrder(order);
        return order;
    }
}

/// <summary>
/// An execution engine wired to a real bus, cache, portfolio and test clock with one recording client per venue.
/// </summary>
internal sealed class ExecHarness
{
    public ExecHarness(ExecutionEngineConfig? config = null, OmsType clientOms = OmsType.Netting, bool registerClient = true)
    {
        Clock = new TestClock(TestOrders.T0);
        Bus = new MessageBus(TestIds.Trader);
        Cache = new Cache();
        Portfolio = new Portfolio(Cache, Bus);
        Portfolio.Initialize(Clock, NullLoggerFactory.Instance);
        Engine = new ExecutionEngine(Bus, Cache, config);
        Engine.Initialize(Clock, NullLoggerFactory.Instance);
        Services = new KernelServices(Clock, Cache, Bus, NullLoggerFactory.Instance, TestIds.Trader, TradingEnvironment.Backtest);
        Client = new RecordingExecutionClient(Services, TestIds.Binance, clientOms);
        if (registerClient)
        {
            Engine.RegisterClient(Client);
        }

        Bus.Subscribe(Topics.AllOrderEvents, OrderEvents.Handle);
        Bus.Subscribe(Topics.AllPositionEvents, PositionEvents.Handle);
        Cache.AddInstrument(TestInstruments.BtcUsdt());
        Cache.AddInstrument(TestInstruments.EthUsdt());
    }

    public TestClock Clock { get; }

    public MessageBus Bus { get; }

    public Cache Cache { get; }

    public Portfolio Portfolio { get; }

    public ExecutionEngine Engine { get; }

    public KernelServices Services { get; }

    public RecordingExecutionClient Client { get; }

    public BusRecorder OrderEvents { get; } = new();

    public BusRecorder PositionEvents { get; } = new();

    public SubmitOrder Submit(Order order, PositionId? positionId = null, ClientId? clientId = null, ExecAlgorithmId? execAlgorithmId = null)
    {
        SubmitOrder command = new(order.TraderId, order.StrategyId, order, positionId, execAlgorithmId, clientId, Guid.NewGuid(), Clock.Timestamp);
        Bus.Send(Endpoints.ExecutionEngineExecute, command);
        return command;
    }

    /// <summary>Submits an order and applies Submitted and Accepted, leaving it open at the venue.</summary>
    public T SubmitAndAccept<T>(T order, PositionId? positionId = null) where T : Order
    {
        Submit(order, positionId);
        Engine.Process(TestEvents.Submitted(order));
        Engine.Process(TestEvents.Accepted(order, "V-" + order.ClientOrderId.Value));
        return order;
    }

    /// <summary>Submits a market order and fills it completely at the given price.</summary>
    public MarketOrder FillMarket(string clientOrderId, OrderSide side, string quantity, string price, string commission = "0", StrategyId? strategyId = null, InstrumentId? instrumentId = null, PositionId? commandPositionId = null)
    {
        MarketOrder order = TestOrders.Market(clientOrderId, instrumentId ?? TestIds.BtcUsdt, side, quantity, strategyId);
        SubmitAndAccept(order, commandPositionId);
        Engine.Process(TestEvents.Filled(order, "T-" + clientOrderId, quantity, price, commission));
        return order;
    }
}

/// <summary>
/// A complete backtest-context kernel on a test clock with one recording execution client for BINANCE.
/// Strategies added here run through the real risk and execution engines.
/// </summary>
internal sealed class KernelHarness : IDisposable
{
    public KernelHarness(RiskEngineConfig? risk = null, OmsType clientOms = OmsType.Netting)
    {
        Clock = new TestClock(TestOrders.T0);
        Kernel = new CoreKernel(new KernelConfig { TraderId = TestIds.Trader, RiskEngine = risk ?? new RiskEngineConfig(), InstanceId = "test" }, Clock);
        Client = new RecordingExecutionClient(Kernel.Services, TestIds.Binance, clientOms);
        Kernel.AddExecutionClient(Client);
        Kernel.Cache.AddInstrument(TestInstruments.BtcUsdt());
        Kernel.Cache.AddInstrument(TestInstruments.EthUsdt());
    }

    public TestClock Clock { get; }

    public CoreKernel Kernel { get; }

    public RecordingExecutionClient Client { get; }

    public Cache Cache => Kernel.Cache;

    public ProbeStrategy AddStrategy(StrategyConfig? config = null)
    {
        ProbeStrategy strategy = new(config ?? new StrategyConfig { StrategyId = TestIds.Strategy });
        Kernel.Trader.AddStrategy(strategy);
        return strategy;
    }

    /// <summary>Adds a strategy with the default test id, starts the kernel and clears the start-up log.</summary>
    public ProbeStrategy StartWithStrategy(StrategyConfig? config = null)
    {
        ProbeStrategy strategy = AddStrategy(config);
        Kernel.Start();
        strategy.Calls.Clear();
        return strategy;
    }

    /// <summary>Plays the venue: acknowledges and accepts an order that the client has received.</summary>
    public void Accept(Order order)
    {
        Client.EmitSubmitted(order);
        Client.EmitAccepted(order, "V-" + order.ClientOrderId.Value);
    }

    public void Dispose() => Kernel.Dispose();
}
