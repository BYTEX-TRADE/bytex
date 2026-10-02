using Bytex.Live.Tests.Support;
using System.Text;
using System.Text.Json;
using Bytex.Core.Messaging;
using Bytex.Core.Model.Identifiers;
using Bytex.Persistence.Redis;

namespace Bytex.Live.Tests.Persistence;

// Why (R1.11): a node knows a great deal that nothing outside the process can see. Publishing the bus to Redis
// streams is how a dashboard, a recorder or another program reads it without being hosted inside the trading loop.
//
// Two decisions in it are worth more than the plumbing, and both are pinned here.
//
// The wire record carries the payload's TYPE beside the payload. A reader cannot infer one from a topic - several
// kinds of message share a topic - so a consumer guessing from the topic decodes the wrong shape the day a second
// kind appears on it.
//
// And a full queue DROPS rather than waiting. The alternative is back-pressure, which means a slow or unreachable
// Redis stalls the thread that is trading: a dashboard falling behind would slow the orders it is watching. What is
// dropped is counted, because silently losing messages while appearing to publish them is the one behaviour worse
// than dropping them.
public sealed class RedisBusStreamTests
{
    private sealed record Priced(string Symbol, decimal Price);

    private sealed record Noted(string Text);

    private static RedisBusStreamConfig Config(IEnumerable<string> topics, int queue = 1_000, int maxLength = 100) => new()
    {
        ModuleHostId = "TESTER-001",
        Topics = [.. topics],
        QueueCapacity = queue,
        MaxLength = maxLength,
    };

    private static async Task<T> EventuallyAsync<T>(Func<T> read, Func<T, bool> until)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            T value = read();
            if (until(value))
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.Fail("the condition never came about");
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_published_message_reaches_the_stream_for_its_topic()
    {
        MemoryRedisStore store = new();
        MessageBus bus = new(new ModuleHostId("TESTER-001"));
        await using RedisBusStream stream = new(Config(["data.quotes"]), bus, store);

        bus.Publish("data.quotes", new Priced("BTCUSDT", 50_000m));

        await EventuallyAsync(() => stream.Published, published => published == 1);

        // One stream per topic, so a reader subscribes to what it wants rather than filtering everything.
        List<IReadOnlyDictionary<string, byte[]>> entries = store.Streams["bytex:TESTER-001:stream:data.quotes"];
        IReadOnlyDictionary<string, byte[]> entry = Assert.Single(entries);

        Assert.Equal("data.quotes", Encoding.UTF8.GetString(entry["topic"]));
        Assert.Equal(RedisBusStream.JsonEncoding, Encoding.UTF8.GetString(entry["encoding"]));
        Assert.Contains(nameof(Priced), Encoding.UTF8.GetString(entry["type"]), StringComparison.Ordinal);

        Priced? read = JsonSerializer.Deserialize<Priced>(entry["payload"], Core.Serialization.BytexJson.Options);

        Assert.Equal(new Priced("BTCUSDT", 50_000m), read);
    }

    [Fact]
    public async Task The_type_travels_with_the_payload_so_a_reader_need_not_guess_from_the_topic()
    {
        // The case that makes it necessary: two kinds of message on one topic. A consumer keying off the topic alone
        // decodes the second as the first, and gets a plausible object out of the wrong bytes.
        MemoryRedisStore store = new();
        MessageBus bus = new(new ModuleHostId("TESTER-001"));
        await using RedisBusStream stream = new(Config(["events"]), bus, store);

        bus.Publish("events", new Priced("ETHUSDT", 3_000m));
        bus.Publish("events", new Noted("halted"));

        await EventuallyAsync(() => stream.Published, published => published == 2);

        string[] types = [.. store.Streams["bytex:TESTER-001:stream:events"].Select(e => Encoding.UTF8.GetString(e["type"]))];

        Assert.Contains(types, t => t.Contains(nameof(Priced), StringComparison.Ordinal));
        Assert.Contains(types, t => t.Contains(nameof(Noted), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_published_for_a_topic_nobody_asked_for()
    {
        // Publishing everything a busy node says would be a decision made for somebody rather than by them, so the
        // default is nothing and a topic is opted into.
        MemoryRedisStore store = new();
        MessageBus bus = new(new ModuleHostId("TESTER-001"));
        await using RedisBusStream stream = new(Config(["data.quotes"]), bus, store);

        bus.Publish("orders", new Noted("submitted"));
        bus.Publish("data.quotes", new Priced("BTCUSDT", 1m));

        await EventuallyAsync(() => stream.Published, published => published == 1);

        Assert.Single(store.Streams);
        Assert.False(store.Streams.ContainsKey("bytex:TESTER-001:stream:orders"));
    }

    [Fact]
    public async Task A_stream_nobody_reads_stops_growing_rather_than_filling_the_server()
    {
        MemoryRedisStore store = new();
        MessageBus bus = new(new ModuleHostId("TESTER-001"));
        await using RedisBusStream stream = new(Config(["data.quotes"], maxLength: 5), bus, store);

        for (int i = 0; i < 25; i++)
        {
            bus.Publish("data.quotes", new Priced("BTCUSDT", i));
        }

        await EventuallyAsync(() => stream.Published, published => published == 25);

        // Trimmed oldest-first, so what is kept is the most recent - the part a reader coming back wants.
        List<IReadOnlyDictionary<string, byte[]>> entries = store.Streams["bytex:TESTER-001:stream:data.quotes"];

        Assert.Equal(5, entries.Count);
        Assert.Equal(24m, JsonSerializer.Deserialize<Priced>(entries[^1]["payload"], Core.Serialization.BytexJson.Options)!.Price);
    }

    [Fact]
    public async Task A_message_that_cannot_be_serialised_costs_one_message_and_not_the_rest()
    {
        MemoryRedisStore store = new();
        MessageBus bus = new(new ModuleHostId("TESTER-001"));
        await using RedisBusStream stream = new(Config(["events"]), bus, store);

        // A type that cannot be written as JSON, published between two that can.
        bus.Publish("events", new Noted("before"));
        bus.Publish("events", new Func<int>(() => 1));
        bus.Publish("events", new Noted("after"));

        await EventuallyAsync(() => stream.Published, published => published == 2);

        Assert.Equal(2, store.Streams["bytex:TESTER-001:stream:events"].Count);
    }

    [Fact]
    public async Task Disposing_stops_the_node_talking_to_a_stream_it_no_longer_writes()
    {
        // A publisher that left its subscriptions behind would keep serialising into a queue nobody drains, for the
        // life of the node.
        MemoryRedisStore store = new();
        MessageBus bus = new(new ModuleHostId("TESTER-001"));
        RedisBusStream stream = new(Config(["events"]), bus, store);

        bus.Publish("events", new Noted("one"));
        await EventuallyAsync(() => stream.Published, published => published == 1);
        await stream.DisposeAsync();

        bus.Publish("events", new Noted("two"));

        Assert.Single(store.Streams["bytex:TESTER-001:stream:events"]);
        Assert.DoesNotContain("events", bus.Topics);
    }
}
