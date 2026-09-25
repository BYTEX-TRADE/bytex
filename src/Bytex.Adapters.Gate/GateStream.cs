using System.Globalization;
using System.Text.Json;
using Bytex.Live.Network;

namespace Bytex.Adapters.Gate;

/// <summary>
/// The shape of every message on a Gate socket, which is one shape for all three markets: an object carrying a
/// <c>time</c> in seconds, a <c>channel</c>, an <c>event</c> and a <c>payload</c> or a <c>result</c>.
/// <para>
/// The channel names are prefixed by market - <c>spot.</c> on spot, <c>futures.</c> on BOTH derivative markets,
/// because delivery answers on its own address with the same prefix - so a client knows its prefix and nothing else
/// here needs to.
/// </para>
/// </summary>
internal static class GateStream
{
    /// <summary>The channel prefix on the spot socket.</summary>
    public const string SpotPrefix = "spot.";

    /// <summary>
    /// The channel prefix on both derivative sockets. Delivery uses the same prefix as perpetual futures, on its own
    /// address - measured, because a delivery-specific prefix is the obvious guess and it is refused.
    /// </summary>
    public const string FuturesPrefix = "futures.";

    /// <summary>
    /// The keep-alive. It is an application message on a <c>.ping</c> channel rather than a WebSocket control frame,
    /// and the venue answers on <c>.pong</c>. The <c>time</c> it carries is not checked - a ping sent with zero is
    /// answered - which is why one fixed string can be sent for the life of the connection.
    /// </summary>
    public static string Ping(string prefix) =>
        JsonSerializer.Serialize(new { time = 0, channel = prefix + "ping" });

    /// <summary>A subscribe or unsubscribe request for one channel and payload.</summary>
    public static string Subscribe(string channel, IReadOnlyList<string> payload, bool subscribe = true) =>
        JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            channel,
            @event = subscribe ? "subscribe" : "unsubscribe",
            payload,
        });

    /// <summary>
    /// The credential a private channel is opened with: the key, and a hex HMAC-SHA512 over the channel, the event
    /// and the time. It is not the REST signature and shares none of its lines with it, which is why it is here
    /// rather than on the http client.
    /// </summary>
    public static string SubscribePrivate(string channel, string payloadJson, GateCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signed = $"channel={channel}&event=subscribe&time={time.ToString(CultureInfo.InvariantCulture)}";
        return $"{{\"time\":{time.ToString(CultureInfo.InvariantCulture)},\"channel\":\"{channel}\",\"event\":\"subscribe\","
            + $"\"payload\":{payloadJson},"
            + $"\"auth\":{{\"method\":\"api_key\",\"KEY\":{JsonSerializer.Serialize(credentials.Key)},"
            + $"\"SIGN\":\"{HmacSigner.Sha512Hex(credentials.Secret, signed)}\"}}}}";
    }

    /// <summary>
    /// What the venue calls the channel of one kind of data, given a market's prefix. Named here so the subscribe
    /// path and the dispatch path cannot drift apart: a client that subscribes to one spelling and switches on
    /// another connects, is told the subscription succeeded, and receives nothing anybody looks at.
    /// </summary>
    public const string BookTicker = "book_ticker";

    public const string Trades = "trades";

    public const string Candlesticks = "candlesticks";

    /// <summary>The depth SNAPSHOT channel, which carries the whole of the top N levels each time.</summary>
    public const string OrderBook = "order_book";

    public const string Tickers = "tickers";

    /// <summary>
    /// The event name on a depth snapshot, which is not the same word on the two markets: spot sends
    /// <c>update</c> and the derivative markets send <c>all</c>. A client that filtered on one word would silently
    /// drop every book message on the other market.
    /// </summary>
    public const string UpdateEvent = "update";

    /// <summary>The derivative markets' word for a whole snapshot; see <see cref="UpdateEvent"/>.</summary>
    public const string SnapshotEvent = "all";

    /// <summary>
    /// How many levels a depth subscription asks for. The venue takes the count as a string in the payload and
    /// offers a fixed set of them; twenty is the deepest of the small ones and is what a top-of-book quote needs.
    /// </summary>
    public const string DepthLevels = "20";

    /// <summary>
    /// How often the venue pushes a depth snapshot on spot. The venue takes an interval rather than "as it changes",
    /// and offers 100ms and 1000ms; the faster one is asked for so a quote is as fresh as the venue will make it.
    /// </summary>
    public const string SpotDepthInterval = "100ms";

    /// <summary>
    /// The same field on the derivative markets, where the venue takes <c>0</c> to mean "push it as it changes".
    /// Measured: it pushes roughly ten times a second on a busy contract.
    /// </summary>
    public const string FuturesDepthInterval = "0";
}
