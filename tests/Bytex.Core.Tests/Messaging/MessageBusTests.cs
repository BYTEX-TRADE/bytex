using Bytex.Core.Messaging;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Messaging;

// Why: R1.3 and R1.1 - every component talks through this bus, so delivery order must be deterministic
// (priority, then subscription order) and one failing handler must not starve the others.
public class MessageBusTests
{
    private sealed record Ping(Guid Id, string CallbackEndpoint, UnixNanos TsInit) : Request(Id, CallbackEndpoint, TsInit);

    private sealed record Pong(Guid CorrelationId, UnixNanos TsInit, string Payload) : Response(CorrelationId, TsInit);

    private static MessageBus NewBus(Action<Exception, object, object>? onError = null) => new(TestIds.Trader, null, onError);

    [Fact]
    public void Publish_delivers_to_exact_topic_subscribers_only()
    {
        MessageBus bus = NewBus();
        List<object> a = new();
        List<object> b = new();
        bus.Subscribe("data.quotes.BINANCE.BTCUSDT", a.Add);
        bus.Subscribe("data.quotes.BINANCE.ETHUSDT", b.Add);

        bus.Publish("data.quotes.BINANCE.BTCUSDT", "tick");

        Assert.Equal(["tick"], a);
        Assert.Empty(b);
        Assert.Equal(1, bus.PublishedCount);
    }

    [Theory]
    [InlineData("data.*", "data.quotes.BINANCE.BTCUSDT", true)]
    [InlineData("data.quotes.*.BTCUSDT", "data.quotes.BINANCE.BTCUSDT", true)]
    [InlineData("data.quotes.*.BTCUSDT", "data.quotes.BINANCE.ETHUSDT", false)]
    [InlineData("*", "anything.at.all", true)]
    [InlineData("events.order.S-00?", "events.order.S-001", true)]
    [InlineData("events.order.S-00?", "events.order.S-0012", false)] // '?' is exactly one character
    [InlineData("events.order.S-00?", "events.order.S-00", false)]
    [InlineData("data.quotes", "data.quotes.BINANCE.BTCUSDT", false)] // no implicit prefix matching
    [InlineData("data.quotes.BINANCE.BTCUSDT", "data.quotes.BINANCE.BTCUSDT.extra", false)]
    [InlineData("data.q*", "data+quotes", false)] // '.' in a pattern is a literal dot, not a regex wildcard
    [InlineData("DATA.*", "data.quotes", false)] // topics are case sensitive
    public void Wildcard_patterns_match_as_documented(string pattern, string topic, bool expected)
    {
        MessageBus bus = NewBus();
        bus.Subscribe(pattern, _ => { });

        Assert.Equal(expected, bus.HasSubscribers(topic));
    }

    [Fact]
    public void Handlers_run_in_subscription_order_across_exact_and_wildcard_subscriptions()
    {
        MessageBus bus = NewBus();
        List<string> order = new();
        bus.Subscribe("events.order.*", _ => order.Add("first:wildcard"));
        bus.Subscribe("events.order.S-001", _ => order.Add("second:exact"));
        bus.Subscribe("events.*", _ => order.Add("third:wildcard"));

        bus.Publish("events.order.S-001", "e");

        Assert.Equal(["first:wildcard", "second:exact", "third:wildcard"], order);
    }

    [Fact]
    public void Higher_priority_handlers_run_first_and_equal_priorities_keep_subscription_order()
    {
        MessageBus bus = NewBus();
        List<string> order = new();
        bus.Subscribe("t", _ => order.Add("p0-a"));
        bus.Subscribe("t", _ => order.Add("p10"), priority: 10);
        bus.Subscribe("t", _ => order.Add("p0-b"));
        bus.Subscribe("t", _ => order.Add("p-5"), priority: -5);

        bus.Publish("t", "m");

        Assert.Equal(["p10", "p0-a", "p0-b", "p-5"], order);
    }

    [Fact]
    public void Subscription_added_after_a_publish_is_honoured_by_the_next_publish()
    {
        // Resolved handler lists are cached per topic; a new subscription must invalidate that cache.
        MessageBus bus = NewBus();
        List<object> late = new();
        bus.Subscribe("t", _ => { });
        bus.Publish("t", 1);

        bus.Subscribe("t", late.Add);
        bus.Publish("t", 2);

        Assert.Equal([2], late);
    }

    [Fact]
    public void Same_handler_subscribed_twice_to_a_topic_is_called_once()
    {
        MessageBus bus = NewBus();
        List<object> received = new();
        Action<object> handler = received.Add;

        bus.Subscribe("t", handler);
        bus.Subscribe("t", handler);
        bus.Publish("t", "m");

        Assert.Single(received);
    }

    [Fact]
    public void Unsubscribe_stops_delivery_and_removes_the_topic()
    {
        MessageBus bus = NewBus();
        List<object> received = new();
        Action<object> handler = received.Add;
        bus.Subscribe("t", handler);
        bus.Publish("t", 1);

        bus.Unsubscribe("t", handler);
        bus.Publish("t", 2);

        Assert.Equal([1], received);
        Assert.False(bus.HasSubscribers("t"));
        Assert.DoesNotContain("t", bus.Topics);
    }

    [Fact]
    public void Unsubscribe_removes_only_the_given_handler()
    {
        MessageBus bus = NewBus();
        List<object> kept = new();
        List<object> removed = new();
        Action<object> removedHandler = removed.Add;
        bus.Subscribe("t", kept.Add);
        bus.Subscribe("t", removedHandler);

        bus.Unsubscribe("t", removedHandler);
        bus.Publish("t", "m");

        Assert.Single(kept);
        Assert.Empty(removed);
        Assert.Contains("t", bus.Topics);
    }

    [Fact]
    public void Unsubscribe_of_something_never_subscribed_is_harmless()
    {
        MessageBus bus = NewBus();

        bus.Unsubscribe("never", _ => { });

        Assert.Empty(bus.Topics);
    }

    [Fact]
    public void Publish_without_subscribers_is_counted_and_otherwise_does_nothing()
    {
        MessageBus bus = NewBus();

        bus.Publish("nobody.listens", "m");

        Assert.Equal(1, bus.PublishedCount);
    }

    [Fact]
    public void Throwing_handler_does_not_stop_later_handlers_and_is_reported()
    {
        List<(Exception Error, object Message)> reported = new();
        MessageBus bus = NewBus((e, _, m) => reported.Add((e, m)));
        List<object> after = new();
        bus.Subscribe("t", _ => throw new InvalidOperationException("boom"));
        bus.Subscribe("t", after.Add);

        bus.Publish("t", "m");

        Assert.Equal(["m"], after);
        (Exception error, object message) = Assert.Single(reported);
        Assert.Equal("boom", error.Message);
        Assert.Equal("m", message);
    }

    [Fact]
    public void Handler_may_publish_from_inside_a_handler_and_nested_delivery_completes_first()
    {
        MessageBus bus = NewBus();
        List<string> order = new();
        bus.Subscribe("outer", _ =>
        {
            order.Add("outer:start");
            bus.Publish("inner", "x");
            order.Add("outer:end");
        });
        bus.Subscribe("inner", _ => order.Add("inner"));

        bus.Publish("outer", "x");

        Assert.Equal(["outer:start", "inner", "outer:end"], order);
    }

    [Fact]
    public void Send_delivers_to_the_registered_endpoint()
    {
        MessageBus bus = NewBus();
        List<object> received = new();
        bus.Register("RiskEngine.execute", received.Add);

        bus.Send("RiskEngine.execute", "command");

        Assert.Equal(["command"], received);
        Assert.Equal(1, bus.SentCount);
        Assert.True(bus.IsRegistered("RiskEngine.execute"));
        Assert.Contains("RiskEngine.execute", bus.Endpoints);
    }

    [Fact]
    public void Send_to_an_unknown_endpoint_is_dropped_and_not_counted()
    {
        MessageBus bus = NewBus();

        bus.Send("Nobody.home", "command");

        Assert.Equal(0, bus.SentCount);
    }

    [Fact]
    public void Send_does_not_reach_topic_subscribers_with_the_same_name()
    {
        MessageBus bus = NewBus();
        List<object> subscriber = new();
        bus.Subscribe("name", subscriber.Add);
        bus.Register("name", _ => { });

        bus.Send("name", "m");

        Assert.Empty(subscriber);
    }

    [Fact]
    public void Registering_an_endpoint_again_replaces_its_handler()
    {
        MessageBus bus = NewBus();
        List<object> first = new();
        List<object> second = new();
        bus.Register("e", first.Add);

        bus.Register("e", second.Add);
        bus.Send("e", "m");

        Assert.Empty(first);
        Assert.Single(second);
    }

    [Fact]
    public void Deregistered_endpoint_no_longer_receives()
    {
        MessageBus bus = NewBus();
        List<object> received = new();
        bus.Register("e", received.Add);

        bus.Deregister("e");
        bus.Send("e", "m");

        Assert.Empty(received);
        Assert.False(bus.IsRegistered("e"));
    }

    [Fact]
    public void Throwing_endpoint_is_contained_and_reported()
    {
        List<Exception> reported = new();
        MessageBus bus = NewBus((e, _, _) => reported.Add(e));
        bus.Register("e", _ => throw new InvalidOperationException("boom"));

        bus.Send("e", "m");

        Assert.Equal("boom", Assert.Single(reported).Message);
    }

    [Fact]
    public void Response_is_delivered_to_the_callback_of_the_request_with_the_same_id()
    {
        MessageBus bus = NewBus();
        bus.Register("service", m =>
        {
            Ping ping = (Ping)m;
            bus.Respond(new Pong(ping.Id, UnixNanos.Zero, "pong for " + ping.CallbackEndpoint));
        });
        List<Response> first = new();
        List<Response> second = new();

        bus.Request("service", new Ping(Guid.NewGuid(), "A", UnixNanos.Zero), first.Add);
        bus.Request("service", new Ping(Guid.NewGuid(), "B", UnixNanos.Zero), second.Add);

        Assert.Equal("pong for A", Assert.IsType<Pong>(Assert.Single(first)).Payload);
        Assert.Equal("pong for B", Assert.IsType<Pong>(Assert.Single(second)).Payload);
    }

    [Fact]
    public void Responses_arriving_out_of_order_still_reach_the_right_callbacks()
    {
        MessageBus bus = NewBus();
        List<Ping> pending = new();
        bus.Register("service", m => pending.Add((Ping)m));
        List<string> delivered = new();
        bus.Request("service", new Ping(Guid.NewGuid(), "A", UnixNanos.Zero), r => delivered.Add("A<-" + ((Pong)r).Payload));
        bus.Request("service", new Ping(Guid.NewGuid(), "B", UnixNanos.Zero), r => delivered.Add("B<-" + ((Pong)r).Payload));

        bus.Respond(new Pong(pending[1].Id, UnixNanos.Zero, "b"));
        bus.Respond(new Pong(pending[0].Id, UnixNanos.Zero, "a"));

        Assert.Equal(["B<-b", "A<-a"], delivered);
    }

    [Fact]
    public void Callback_fires_once_even_if_the_response_is_repeated()
    {
        MessageBus bus = NewBus();
        Ping? seen = null;
        bus.Register("service", m => seen = (Ping)m);
        int calls = 0;
        bus.Request("service", new Ping(Guid.NewGuid(), "A", UnixNanos.Zero), _ => calls++);

        bus.Respond(new Pong(seen!.Id, UnixNanos.Zero, "1"));
        bus.Respond(new Pong(seen.Id, UnixNanos.Zero, "2"));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Response_without_a_pending_request_is_ignored()
    {
        MessageBus bus = NewBus();

        bus.Respond(new Pong(Guid.NewGuid(), UnixNanos.Zero, "orphan"));

        Assert.Equal(0, bus.SentCount);
    }

    [Fact]
    public void Throwing_response_callback_is_contained_and_reported()
    {
        List<Exception> reported = new();
        MessageBus bus = NewBus((e, _, _) => reported.Add(e));
        bus.Register("service", m => bus.Respond(new Pong(((Ping)m).Id, UnixNanos.Zero, "x")));

        bus.Request("service", new Ping(Guid.NewGuid(), "A", UnixNanos.Zero), _ => throw new InvalidOperationException("boom"));

        Assert.Equal("boom", Assert.Single(reported).Message);
    }

    [Fact]
    public void Arguments_are_validated()
    {
        MessageBus bus = NewBus();

        Assert.Throws<ArgumentException>(() => bus.Subscribe(" ", _ => { }));
        Assert.Throws<ArgumentNullException>(() => bus.Subscribe("t", null!));
        Assert.Throws<ArgumentException>(() => bus.Publish("", "m"));
        Assert.Throws<ArgumentNullException>(() => bus.Publish("t", null!));
        Assert.Throws<ArgumentException>(() => bus.Register("", _ => { }));
        Assert.Throws<ArgumentNullException>(() => bus.Send("e", null!));
    }

    [Fact]
    public void Two_buses_fed_the_same_script_deliver_in_the_same_order()
    {
        static List<string> Run()
        {
            MessageBus bus = new(TestIds.Trader);
            List<string> log = new();
            bus.Subscribe("a.*", m => log.Add($"a.*:{m}"), priority: 1);
            bus.Subscribe("a.b", m => log.Add($"a.b:{m}"));
            bus.Subscribe("*", m => log.Add($"*:{m}"), priority: 5);
            bus.Subscribe("a.?", m => log.Add($"a.?:{m}"), priority: 1);
            bus.Publish("a.b", 1);
            bus.Publish("a.c", 2);
            bus.Publish("z", 3);
            return log;
        }

        List<string> first = Run();
        List<string> second = Run();

        Assert.Equal(["*:1", "a.*:1", "a.?:1", "a.b:1", "*:2", "a.*:2", "a.?:2", "*:3"], first);
        Assert.Equal(first, second);
    }
}
