using System.Text.Json;
using System.Text.Json.Serialization;
using Bytex.Core.Model.Events;
using Bytex.Core.Serialization;

namespace Bytex.Persistence.Shared;

/// <summary>
/// Serializes order and account events with a type discriminator so they can be replayed.
///
/// <para>
/// Internal, and compiled into each persistence library: one public type of the same name in two assemblies is a
/// collision for anybody referencing both. The published <c>Bytex.Persistence.Redis.EventSerializer</c> stays
/// where it has always been and forwards here, so nothing that shipped moves.
/// </para>
/// </summary>
internal static class EventSerializer
{
    private static readonly JsonSerializerOptions Options = new(BytexJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly Dictionary<string, Type> OrderEventTypes = new(StringComparer.Ordinal)
    {
        [nameof(OrderInitialized)] = typeof(OrderInitialized),
        [nameof(OrderDenied)] = typeof(OrderDenied),
        [nameof(OrderEmulated)] = typeof(OrderEmulated),
        [nameof(OrderReleased)] = typeof(OrderReleased),
        [nameof(OrderSubmitted)] = typeof(OrderSubmitted),
        [nameof(OrderAccepted)] = typeof(OrderAccepted),
        [nameof(OrderRejected)] = typeof(OrderRejected),
        [nameof(OrderCanceled)] = typeof(OrderCanceled),
        [nameof(OrderExpired)] = typeof(OrderExpired),
        [nameof(OrderTriggered)] = typeof(OrderTriggered),
        [nameof(OrderPendingUpdate)] = typeof(OrderPendingUpdate),
        [nameof(OrderPendingCancel)] = typeof(OrderPendingCancel),
        [nameof(OrderModifyRejected)] = typeof(OrderModifyRejected),
        [nameof(OrderCancelRejected)] = typeof(OrderCancelRejected),
        [nameof(OrderUpdated)] = typeof(OrderUpdated),
        [nameof(OrderFilled)] = typeof(OrderFilled),
    };

    public static string Serialize(Event e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Envelope envelope = new(e.GetType().Name, JsonSerializer.SerializeToElement(e, e.GetType(), Options));
        return JsonSerializer.Serialize(envelope, Options);
    }

    public static OrderEvent? DeserializeOrderEvent(string json)
    {
        Envelope? envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        if (envelope is null || !OrderEventTypes.TryGetValue(envelope.Type, out Type? type))
        {
            return null;
        }

        return (OrderEvent?)JsonSerializer.Deserialize(envelope.Payload.GetRawText(), type, Options);
    }

    public static AccountState? DeserializeAccountState(string json)
    {
        Envelope? envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        if (envelope is null || envelope.Type != nameof(AccountState))
        {
            return null;
        }

        return JsonSerializer.Deserialize<AccountState>(envelope.Payload.GetRawText(), Options);
    }

    private sealed record Envelope(string Type, JsonElement Payload);
}
