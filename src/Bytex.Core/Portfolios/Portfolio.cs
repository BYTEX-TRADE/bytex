using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Portfolios;

/// <summary>
/// Aggregated view of accounts, positions, exposures, and profit and loss across venues.
/// </summary>
public interface IPortfolio
{
    Account? Account(Venue venue);

    IReadOnlyDictionary<Currency, Money> BalancesLocked(Venue venue);

    IReadOnlyDictionary<Currency, Money> MarginsInit(Venue venue);

    IReadOnlyDictionary<Currency, Money> MarginsMaint(Venue venue);

    IReadOnlyDictionary<Currency, Money> UnrealizedPnls(Venue venue);

    IReadOnlyDictionary<Currency, Money> RealizedPnls(Venue venue);

    IReadOnlyDictionary<Currency, Money> NetExposures(Venue venue);

    Money? UnrealizedPnl(MarketKey marketKey);

    Money? RealizedPnl(MarketKey marketKey);

    Money? TotalPnl(MarketKey marketKey);

    Money? NetExposure(MarketKey marketKey);

    decimal NetPosition(MarketKey marketKey);

    bool IsNetLong(MarketKey marketKey);

    bool IsNetShort(MarketKey marketKey);

    bool IsFlat(MarketKey marketKey);

    bool IsCompletelyFlat();
}

public sealed class Portfolio : Component, IPortfolio
{
    private readonly ICache _cache;
    private readonly Cache? _writableCache;
    private readonly IMessageBus _bus;

    public Portfolio(ICache cache, IMessageBus bus)
        : base(new ComponentId("Portfolio"))
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(bus);
        _cache = cache;
        _writableCache = cache as Cache;
        _bus = bus;
        _bus.Register(Endpoints.PortfolioUpdateAccount, m => UpdateAccount((AccountState)m));
    }

    public Account? Account(Venue venue) => _cache.AccountForVenue(venue);

    /// <summary>
    /// Applies an account state to the cached account (creating it if necessary) and publishes the event.
    /// </summary>
    public void UpdateAccount(AccountState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Account? account = _cache.Account(state.AccountId);
        if (account is null)
        {
            account = state.AccountType == AccountType.Margin ? new MarginAccount(state) : new CashAccount(state);
            _writableCache?.AddAccount(account);
            Log.LogInformation("Initialized account {Account}", account);
        }
        else
        {
            account.Apply(state);
            _writableCache?.UpdateAccount(account);
        }

        _bus.Publish(Topics.AccountEvents(state.AccountId), state);
    }

    public IReadOnlyDictionary<Currency, Money> BalancesLocked(Venue venue)
    {
        Dictionary<Currency, Money> locked = new();
        Account? account = Account(venue);
        if (account is not CashAccount cash)
        {
            return locked;
        }

        foreach (Order order in _cache.OrdersOpen(venue))
        {
            Instrument? instrument = _cache.Instrument(order.MarketKey);
            if (instrument is null)
            {
                continue;
            }

            Price? price = order.Price ?? order.TriggerPrice ?? _cache.Price(order.MarketKey, order.IsBuy ? PriceType.Ask : PriceType.Bid);
            if (price is null)
            {
                continue;
            }

            Money amount = cash.CalculateBalanceLocked(instrument, order.Side, order.LeavesQuantity, price.Value);
            Accumulate(locked, amount);
        }

        return locked;
    }

    public IReadOnlyDictionary<Currency, Money> MarginsInit(Venue venue)
    {
        Dictionary<Currency, Money> result = new();
        if (Account(venue) is MarginAccount margin)
        {
            foreach (MarginBalance balance in margin.Margins.Values)
            {
                Accumulate(result, balance.Initial);
            }
        }

        return result;
    }

    public IReadOnlyDictionary<Currency, Money> MarginsMaint(Venue venue)
    {
        Dictionary<Currency, Money> result = new();
        if (Account(venue) is MarginAccount margin)
        {
            foreach (MarginBalance balance in margin.Margins.Values)
            {
                Accumulate(result, balance.Maintenance);
            }
        }

        return result;
    }

    public IReadOnlyDictionary<Currency, Money> UnrealizedPnls(Venue venue)
    {
        Dictionary<Currency, Money> result = new();
        foreach (Position position in _cache.PositionsOpen(venue))
        {
            Money? pnl = UnrealizedPnl(position);
            if (pnl is { } p)
            {
                Accumulate(result, p);
            }
        }

        return result;
    }

    public IReadOnlyDictionary<Currency, Money> RealizedPnls(Venue venue)
    {
        Dictionary<Currency, Money> result = new();
        foreach (Position position in _cache.Positions(venue))
        {
            Accumulate(result, position.RealizedPnl);
        }

        return result;
    }

    public IReadOnlyDictionary<Currency, Money> NetExposures(Venue venue)
    {
        Dictionary<Currency, Money> result = new();
        foreach (MarketKey marketKey in _cache.PositionsOpen(venue).Select(p => p.MarketKey).Distinct())
        {
            if (NetExposure(marketKey) is { } exposure)
            {
                Accumulate(result, exposure);
            }
        }

        return result;
    }

    public Money? UnrealizedPnl(MarketKey marketKey)
    {
        IReadOnlyList<Position> positions = _cache.PositionsOpen(marketKey: marketKey);
        if (positions.Count == 0)
        {
            Instrument? instrument = _cache.Instrument(marketKey);
            return instrument is null ? null : Money.Zero(instrument.SettlementCurrency);
        }

        Money? total = null;
        foreach (Position position in positions)
        {
            Money? pnl = UnrealizedPnl(position);
            if (pnl is null)
            {
                return null;
            }

            total = total is { } t ? t + pnl.Value : pnl.Value;
        }

        return total;
    }

    private Money? UnrealizedPnl(Position position)
    {
        PriceType priceType = position.IsLong ? PriceType.Bid : PriceType.Ask;
        Price? last = _cache.Price(position.MarketKey, priceType) ?? _cache.Price(position.MarketKey, PriceType.Last);
        return last is { } price ? position.UnrealizedPnl(price) : null;
    }

    public Money? RealizedPnl(MarketKey marketKey)
    {
        IReadOnlyList<Position> positions = _cache.Positions(marketKey: marketKey);
        Instrument? instrument = _cache.Instrument(marketKey);
        if (instrument is null)
        {
            return null;
        }

        Money total = Money.Zero(instrument.SettlementCurrency);
        foreach (Position position in positions)
        {
            total += position.RealizedPnl;
        }

        return total;
    }

    public Money? TotalPnl(MarketKey marketKey)
    {
        Money? realized = RealizedPnl(marketKey);
        Money? unrealized = UnrealizedPnl(marketKey);
        if (realized is null || unrealized is null)
        {
            return null;
        }

        return realized.Value + unrealized.Value;
    }

    public Money? NetExposure(MarketKey marketKey)
    {
        Instrument? instrument = _cache.Instrument(marketKey);
        if (instrument is null)
        {
            return null;
        }

        decimal net = NetPosition(marketKey);
        if (net == 0m)
        {
            return Money.Zero(instrument.CostCurrency);
        }

        Price? last = _cache.Price(marketKey, PriceType.Mid) ?? _cache.Price(marketKey, PriceType.Last);
        if (last is null)
        {
            return null;
        }

        Quantity quantity = new(Math.Abs(net), instrument.SizePrecision);
        return instrument.NotionalValue(quantity, last.Value);
    }

    public decimal NetPosition(MarketKey marketKey)
    {
        decimal net = 0m;
        foreach (Position position in _cache.PositionsOpen(marketKey: marketKey))
        {
            net += position.SignedQuantity;
        }

        return net;
    }

    public bool IsNetLong(MarketKey marketKey) => NetPosition(marketKey) > 0m;

    public bool IsNetShort(MarketKey marketKey) => NetPosition(marketKey) < 0m;

    public bool IsFlat(MarketKey marketKey) => NetPosition(marketKey) == 0m;

    public bool IsCompletelyFlat() => _cache.PositionsOpenCount() == 0;

    private static void Accumulate(Dictionary<Currency, Money> target, Money amount)
    {
        if (target.TryGetValue(amount.Currency, out Money existing))
        {
            target[amount.Currency] = existing + amount;
        }
        else
        {
            target[amount.Currency] = amount;
        }
    }
}
