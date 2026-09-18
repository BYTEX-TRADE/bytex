using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Tests.Model;

/// <summary>
/// Deterministic building blocks shared by the model tests: fixed identifiers, a fixed clock origin,
/// four instruments that cover the pricing models (spot, linear perpetual, inverse perpetual, multiplied future),
/// and builders for orders and order events.
/// </summary>
internal static class ModelFixtures
{
    /// <summary>2023-11-14T22:13:20Z.</summary>
    public const long T0Nanos = 1_700_000_000_000_000_000L;

    public static TraderId Trader { get; } = new("TRADER-001");

    public static StrategyId Strategy { get; } = new("EmaCross-001");

    public static AccountId Account { get; } = new("BINANCE-001");

    public static UnixNanos T0 { get; } = new(T0Nanos);

    /// <summary>A timestamp <paramref name="seconds"/> after <see cref="T0"/>.</summary>
    public static UnixNanos At(long seconds) => new(T0Nanos + (seconds * UnixNanos.NanosPerSecond));

    /// <summary>A deterministic GUID; tests never depend on its value, only on it being stable.</summary>
    public static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static decimal D(string text) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

    public static Money Usdt(string amount) => new(D(amount), Currencies.USDT);

    /// <summary>BTC/USDT spot: price tick 0.01, size step 0.000001, maker 2 bps, taker 5 bps.</summary>
    public static InstrumentSpec BtcUsdtSpec() => new()
    {
        Id = InstrumentId.Parse("BTCUSDT.BINANCE"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 6,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.000001m, 6),
        MakerFee = 0.0002m,
        TakerFee = 0.0005m,
        TsEvent = T0,
        TsInit = T0,
    };

    public static CurrencyPair BtcUsdt() => new(BtcUsdtSpec());

    /// <summary>ETH/USDT linear perpetual: tick 0.01, step 0.001, 5% initial and 2.5% maintenance margin.</summary>
    public static InstrumentSpec EthPerpSpec() => new()
    {
        Id = InstrumentId.Parse("ETHUSDT-PERP.BINANCE"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.ETH,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 2,
        SizePrecision = 3,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.001m, 3),
        MarginInit = 0.05m,
        MarginMaint = 0.025m,
        MakerFee = 0.0002m,
        TakerFee = 0.0005m,
        TsEvent = T0,
        TsInit = T0,
    };

    public static CryptoPerpetual EthPerp() => new(EthPerpSpec());

    /// <summary>XBT/USD inverse perpetual: one contract is 1 USD, settled in BTC, tick 0.5, 1% initial margin.</summary>
    public static InstrumentSpec XbtUsdInverseSpec() => new()
    {
        Id = InstrumentId.Parse("XBTUSD.BITMEX"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USD,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.BTC,
        IsInverse = true,
        PricePrecision = 1,
        SizePrecision = 0,
        PriceIncrement = new Price(0.5m, 1),
        SizeIncrement = new Quantity(1m, 0),
        MarginInit = 0.01m,
        MarginMaint = 0.005m,
        MakerFee = 0.00025m,
        TakerFee = 0.00075m,
        TsEvent = T0,
        TsInit = T0,
    };

    public static CryptoPerpetual XbtUsdInverse() => new(XbtUsdInverseSpec());

    /// <summary>E-mini style future: tick 0.25, whole contracts, 50 USD per point.</summary>
    public static InstrumentSpec EsFutureSpec() => new()
    {
        Id = InstrumentId.Parse("ESZ6.CME"),
        AssetClass = AssetClass.Index,
        InstrumentClass = InstrumentClass.Future,
        QuoteCurrency = Currencies.USD,
        PricePrecision = 2,
        SizePrecision = 0,
        PriceIncrement = new Price(0.25m, 2),
        SizeIncrement = new Quantity(1m, 0),
        Multiplier = new Quantity(50m, 0),
        TsEvent = T0,
        TsInit = T0,
    };

    public static FuturesContract EsFuture() => new(EsFutureSpec(), "ES", T0, At(90L * 24 * 3600), "CME");

    public static OrderParams Params(InstrumentId instrumentId, OrderSide side, string quantity, string clientOrderId = "O-20231114-221320-001-001-1") => new()
    {
        TraderId = Trader,
        StrategyId = Strategy,
        InstrumentId = instrumentId,
        ClientOrderId = new ClientOrderId(clientOrderId),
        Side = side,
        Quantity = Quantity.Parse(quantity),
        InitId = Id(1),
        TsInit = T0,
    };

    public static OrderParams BtcParams(OrderSide side = OrderSide.Buy, string quantity = "10.000000") =>
        Params(InstrumentId.Parse("BTCUSDT.BINANCE"), side, quantity);

    public static OrderDenied Denied(Order o, long t = 1) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, "denied by risk", Id(100), At(t), At(t));

    public static OrderEmulated Emulated(Order o, long t = 1) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, Id(101), At(t), At(t));

    public static OrderReleased Released(Order o, long t = 1) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, Price.Parse("100.00"), Id(102), At(t), At(t));

    public static OrderSubmitted Submitted(Order o, long t = 1) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, Account, Id(103), At(t), At(t));

    public static OrderAccepted Accepted(Order o, long t = 2, string venueOrderId = "V-1") =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, new VenueOrderId(venueOrderId), Account, Id(104), At(t), At(t));

    public static OrderRejected Rejected(Order o, long t = 2) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, Account, "insufficient balance", Id(105), At(t), At(t));

    public static OrderCanceled Canceled(Order o, long t = 9) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, o.VenueOrderId, Account, Id(106), At(t), At(t));

    public static OrderExpired Expired(Order o, long t = 9) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, o.VenueOrderId, Account, Id(107), At(t), At(t));

    public static OrderTriggered Triggered(Order o, long t = 3) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, o.VenueOrderId, Account, Id(108), At(t), At(t));

    public static OrderPendingUpdate PendingUpdate(Order o, long t = 4) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, o.VenueOrderId, Account, Id(109), At(t), At(t));

    public static OrderPendingCancel PendingCancel(Order o, long t = 4) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, o.VenueOrderId, Account, Id(110), At(t), At(t));

    public static OrderModifyRejected ModifyRejected(Order o, long t = 5) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, o.VenueOrderId, Account, "too late", Id(111), At(t), At(t));

    public static OrderCancelRejected CancelRejected(Order o, long t = 5) =>
        new(o.TraderId, o.StrategyId, o.InstrumentId, o.ClientOrderId, o.VenueOrderId, Account, "too late", Id(112), At(t), At(t));

    public static OrderUpdated Updated(Order o, string quantity, string? price = null, string? triggerPrice = null, long t = 5, string? venueOrderId = null) =>
        new(
            o.TraderId,
            o.StrategyId,
            o.InstrumentId,
            o.ClientOrderId,
            venueOrderId is null ? o.VenueOrderId : new VenueOrderId(venueOrderId),
            Account,
            Quantity.Parse(quantity),
            price is null ? null : Price.Parse(price),
            triggerPrice is null ? null : Price.Parse(triggerPrice),
            Id(113),
            At(t),
            At(t));

    /// <summary>A fill for <paramref name="o"/>; quantity and price are text so their precision is explicit.</summary>
    public static OrderFilled Fill(Order o, string quantity, string price, string tradeId, Money? commission = null, string? positionId = "P-1", long t = 6) =>
        new(
            o.TraderId,
            o.StrategyId,
            o.InstrumentId,
            o.ClientOrderId,
            o.VenueOrderId ?? new VenueOrderId("V-1"),
            Account,
            new TradeId(tradeId),
            positionId is null ? null : new PositionId(positionId),
            o.Side,
            o.Type,
            Quantity.Parse(quantity),
            Price.Parse(price),
            Currencies.USDT,
            commission ?? Money.Zero(Currencies.USDT),
            LiquiditySide.Taker,
            Id(114),
            At(t),
            At(t));

    /// <summary>A fill that is not tied to an order object, for driving positions and accounts directly.</summary>
    public static OrderFilled Fill(
        Instrument instrument,
        OrderSide side,
        string quantity,
        string price,
        string tradeId,
        Money? commission = null,
        long t = 0,
        string clientOrderId = "O-1",
        string? positionId = "P-1",
        bool withAccount = true) =>
        new(
            Trader,
            Strategy,
            instrument.Id,
            new ClientOrderId(clientOrderId),
            new VenueOrderId("V-" + clientOrderId),
            withAccount ? Account : null,
            new TradeId(tradeId),
            positionId is null ? null : new PositionId(positionId),
            side,
            OrderType.Market,
            Quantity.Parse(quantity),
            Price.Parse(price),
            instrument.SettlementCurrency,
            commission ?? Money.Zero(instrument.SettlementCurrency),
            LiquiditySide.Taker,
            Id(200),
            At(t),
            At(t));
}
