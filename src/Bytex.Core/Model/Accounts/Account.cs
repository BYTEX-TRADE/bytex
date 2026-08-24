using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Accounts;

/// <summary>
/// An account at a venue. State evolves by applying <see cref="AccountState"/> events.
/// </summary>
public abstract class Account
{
    private readonly List<AccountState> _events = new();
    private readonly Dictionary<Currency, AccountBalance> _balances = new();

    protected Account(AccountState initial, bool calculateAccountState)
    {
        ArgumentNullException.ThrowIfNull(initial);
        Id = initial.AccountId;
        Type = initial.AccountType;
        BaseCurrency = initial.BaseCurrency;
        Calculated = calculateAccountState;
        Apply(initial);
    }

    public AccountId Id { get; }

    public AccountType Type { get; }

    public Currency? BaseCurrency { get; }

    /// <summary>True when the engine computes balances itself rather than relying on venue reports.</summary>
    public bool Calculated { get; }

    public bool IsMultiCurrency => BaseCurrency is null;

    public IReadOnlyDictionary<Currency, AccountBalance> Balances => _balances;

    public IReadOnlyList<AccountState> Events => _events;

    public AccountState LastEvent => _events[^1];

    public int EventCount => _events.Count;

    public IReadOnlyCollection<Currency> Currencies => _balances.Keys;

    public Money? BalanceTotal(Currency? currency = null) => Lookup(currency)?.Total;

    public Money? BalanceFree(Currency? currency = null) => Lookup(currency)?.Free;

    public Money? BalanceLocked(Currency? currency = null) => Lookup(currency)?.Locked;

    public AccountBalance? Balance(Currency? currency = null) => Lookup(currency);

    private AccountBalance? Lookup(Currency? currency)
    {
        Currency? key = currency ?? BaseCurrency;
        if (key is null)
        {
            throw new InvalidOperationException("Currency must be specified for a multi-currency account.");
        }

        return _balances.TryGetValue(key, out AccountBalance? balance) ? balance : null;
    }

    public virtual void Apply(AccountState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.AccountId != Id)
        {
            throw new ArgumentException($"AccountState id {state.AccountId} does not match account {Id}.", nameof(state));
        }

        foreach (AccountBalance balance in state.Balances)
        {
            _balances[balance.Currency] = balance;
        }

        _events.Add(state);
    }

    /// <summary>
    /// Directly replaces a balance (used by the engine when it computes account state itself).
    /// </summary>
    public void UpdateBalance(AccountBalance balance)
    {
        ArgumentNullException.ThrowIfNull(balance);
        _balances[balance.Currency] = balance;
    }

    public void UpdateBalances(IEnumerable<AccountBalance> balances)
    {
        foreach (AccountBalance balance in balances)
        {
            _balances[balance.Currency] = balance;
        }
    }

    /// <summary>
    /// Computes the commission for a fill on this account.
    /// </summary>
    public Money CalculateCommission(Instrument instrument, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide, bool useQuoteForInverse = false)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return instrument.CalculateCommission(lastQty, lastPx, liquiditySide, useQuoteForInverse);
    }

    /// <summary>
    /// Computes the balance changes caused by a fill, in the instrument's currencies.
    /// </summary>
    public abstract IReadOnlyList<Money> CalculatePnls(Instrument instrument, OrderFilled fill, Positions.Position? position);

    public override string ToString() => $"{GetType().Name}(id={Id}, type={Type}, base={BaseCurrency?.Code ?? "None"}, balances=[{string.Join(", ", _balances.Values.Select(b => b.Total))}])";
}

/// <summary>
/// A spot/cash account: buying an asset consumes quote currency and adds base currency.
/// </summary>
public sealed class CashAccount : Account
{
    public CashAccount(AccountState initial, bool calculateAccountState = false)
        : base(initial, calculateAccountState)
    {
    }

    public override IReadOnlyList<Money> CalculatePnls(Instrument instrument, OrderFilled fill, Positions.Position? position)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(fill);

        Currency quote = instrument.QuoteCurrency;
        Currency? baseCurrency = instrument.BaseCurrency;
        decimal notional = fill.LastQty.Value * fill.LastPx.Value * instrument.Multiplier.Value;
        List<Money> pnls = new(2);

        if (baseCurrency is not null)
        {
            Money baseChange = new(fill.IsBuy ? fill.LastQty.Value : -fill.LastQty.Value, baseCurrency);
            pnls.Add(baseChange);
        }

        Money quoteChange = new(fill.IsBuy ? -notional : notional, quote);
        pnls.Add(quoteChange);
        return pnls;
    }

    /// <summary>
    /// Amount of a currency that an open order locks.
    /// </summary>
    public Money CalculateBalanceLocked(Instrument instrument, OrderSide side, Quantity quantity, Price price, bool useQuoteForInverse = false)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (side == OrderSide.Buy)
        {
            Money notional = instrument.NotionalValue(quantity, price, useQuoteForInverse);
            return notional;
        }

        Currency lockCurrency = instrument.BaseCurrency ?? instrument.QuoteCurrency;
        return new Money(quantity.Value * instrument.Multiplier.Value, lockCurrency);
    }
}

/// <summary>
/// A margin account: positions are settled in the instrument's settlement currency with leverage.
/// </summary>
public sealed class MarginAccount : Account
{
    private readonly Dictionary<InstrumentId, decimal> _leverages = new();
    private readonly Dictionary<InstrumentId, MarginBalance> _margins = new();

    public MarginAccount(AccountState initial, bool calculateAccountState = false)
        : base(initial, calculateAccountState)
    {
    }

    public decimal DefaultLeverage { get; set; } = 1m;

    public IReadOnlyDictionary<InstrumentId, decimal> Leverages => _leverages;

    public IReadOnlyDictionary<InstrumentId, MarginBalance> Margins => _margins;

    public void SetDefaultLeverage(decimal leverage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leverage, 1m);
        DefaultLeverage = leverage;
    }

    public void SetLeverage(InstrumentId instrumentId, decimal leverage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leverage, 1m);
        _leverages[instrumentId] = leverage;
    }

    public decimal Leverage(InstrumentId instrumentId) => _leverages.TryGetValue(instrumentId, out decimal leverage) ? leverage : DefaultLeverage;

    public override void Apply(AccountState state)
    {
        base.Apply(state);
        foreach (MarginBalance margin in state.Margins)
        {
            _margins[margin.InstrumentId] = margin;
        }
    }

    public void UpdateMargin(MarginBalance margin)
    {
        ArgumentNullException.ThrowIfNull(margin);
        _margins[margin.InstrumentId] = margin;
    }

    public void ClearMargin(InstrumentId instrumentId) => _margins.Remove(instrumentId);

    public Money CalculateInitialMargin(Instrument instrument, Quantity quantity, Price price, bool useQuoteForInverse = false)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Money notional = instrument.NotionalValue(quantity, price, useQuoteForInverse);
        decimal leverage = Leverage(instrument.Id);
        decimal adjusted = notional.Amount / leverage;
        return new Money(adjusted * instrument.MarginInit, notional.Currency);
    }

    public Money CalculateMaintenanceMargin(Instrument instrument, Quantity quantity, Price price, bool useQuoteForInverse = false)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Money notional = instrument.NotionalValue(quantity, price, useQuoteForInverse);
        decimal leverage = Leverage(instrument.Id);
        decimal adjusted = notional.Amount / leverage;
        return new Money(adjusted * instrument.MarginMaint, notional.Currency);
    }

    public override IReadOnlyList<Money> CalculatePnls(Instrument instrument, OrderFilled fill, Positions.Position? position)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(fill);

        if (position is null || position.EventCount == 0)
        {
            return [];
        }

        // Realized P&L is the change in the position's realized P&L caused by this fill (excluding commission, which is applied separately).
        OrderFilled last = position.LastEvent;
        if (last.TradeId != fill.TradeId)
        {
            return [];
        }

        Money realized = position.RealizedPnl;
        decimal priorRealized = 0m;
        if (position.EventCount > 1)
        {
            priorRealized = PriorRealized(instrument, position);
        }

        Money delta = new(realized.Amount - priorRealized, realized.Currency);
        if (fill.Commission.Currency.Equals(delta.Currency))
        {
            delta += fill.Commission; // commission is deducted separately by the caller
        }

        return delta.IsZero ? [] : [delta];
    }

    private static decimal PriorRealized(Instrument instrument, Positions.Position position)
    {
        // Replay all but the last fill to obtain the realized P&L before this fill.
        Positions.Position replay = new(instrument, position.Events[0]);
        for (int i = 1; i < position.EventCount - 1; i++)
        {
            replay.Apply(position.Events[i]);
        }

        return replay.RealizedPnl.Amount;
    }
}
