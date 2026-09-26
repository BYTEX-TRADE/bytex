using Bytex.Core.Adapters;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Adapters.Gate;

/// <summary>
/// Order routing and execution reporting for Gate's USDT-settled dated contracts.
/// <para>
/// Everything about placing, cancelling and reporting an order is the perpetual market's, on the venue's own
/// delivery paths, which is why this is that client pointed elsewhere rather than a second copy of it: signed
/// sizes in contracts, a market order as a zero-priced immediate-or-cancel, the client order id in the
/// <c>text</c> field, positions signed by side.
/// </para>
/// <para>
/// One thing is genuinely different and it is the one a strategy notices: this market cannot amend an order. The
/// venue's delivery surface has no amend endpoint of any kind - no <c>PUT</c> on an order, no <c>PATCH</c>, no batch
/// amend - where spot amends with <c>PATCH</c> and perpetual futures with <c>PUT</c>. So an amendment is refused
/// here, which is what the family declares, and a strategy that resizes a protective order is told rather than left
/// believing it was resized.
/// </para>
/// </summary>
public sealed class GateDeliveryExecutionClient : GateFuturesExecutionClient
{
    public GateDeliveryExecutionClient(ClientId clientId, GateExecutionClientConfig config, KernelServices services)
        : base(clientId, config, services, GateProductType.Delivery)
    {
    }

    /// <summary>
    /// Refuses the amendment, because the venue has nothing to send it to. A cancel-and-replace here would leave a
    /// dated position unguarded for the length of two requests without the caller having asked for that, so the
    /// caller is told instead and decides for itself.
    /// </summary>
    public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        GenerateOrderModifyRejected(
            Services.Cache.Order(command.ClientOrderId)?.StrategyId ?? new StrategyId("EXTERNAL"),
            command.InstrumentId,
            command.ClientOrderId,
            command.VenueOrderId,
            "Gate's dated contracts cannot change an order once it is placed; cancel it and submit a new one",
            Clock.Timestamp);

        return Task.CompletedTask;
    }
}
