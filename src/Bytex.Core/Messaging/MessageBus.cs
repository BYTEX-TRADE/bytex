using System.Text.RegularExpressions;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Core.Messaging;

public abstract record Request(Guid Id, string CallbackEndpoint, UnixNanos TsInit);

public abstract record Response(Guid CorrelationId, UnixNanos TsInit);

public interface IMessageBus
{
    TraderId TraderId { get; }

    void Subscribe(string topicPattern, Action<object> handler, int priority = 0);

    void Unsubscribe(string topicPattern, Action<object> handler);

    void Publish(string topic, object message);

    void Register(string endpoint, Action<object> handler);

    void Deregister(string endpoint);

    bool IsRegistered(string endpoint);

    void Send(string endpoint, object message);

    void Request(string endpoint, Request request, Action<Response> callback);

    void Respond(Response response);

    bool HasSubscribers(string topic);

    IReadOnlyList<string> Topics { get; }

    IReadOnlyList<string> Endpoints { get; }

    long SentCount { get; }

    long PublishedCount { get; }
}

/// <summary>
/// Single-threaded message bus with pattern subscriptions, point-to-point endpoints, and correlated request/response.
/// </summary>
public sealed class MessageBus : IMessageBus
{
    private readonly Dictionary<string, List<Subscription>> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Subscription[]> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<object>> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Action<Response>> _pending = new();
    private readonly ILogger _log;
    private readonly Action<Exception, object, object>? _onHandlerError;

    public MessageBus(TraderId traderId, ILoggerFactory? loggerFactory = null, Action<Exception, object, object>? onHandlerError = null)
    {
        TraderId = traderId;
        _log = loggerFactory?.CreateLogger<MessageBus>() ?? NullLogger<MessageBus>.Instance;
        _onHandlerError = onHandlerError;
    }

    public TraderId TraderId { get; }

    public long SentCount { get; private set; }

    public long PublishedCount { get; private set; }

    public IReadOnlyList<string> Topics => _subscriptions.Keys.ToList();

    public IReadOnlyList<string> Endpoints => _endpoints.Keys.ToList();

    public void Subscribe(string topicPattern, Action<object> handler, int priority = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicPattern);
        ArgumentNullException.ThrowIfNull(handler);

        if (!_subscriptions.TryGetValue(topicPattern, out List<Subscription>? list))
        {
            list = new List<Subscription>();
            _subscriptions[topicPattern] = list;
        }

        if (list.Any(s => ReferenceEquals(s.Handler, handler) || s.Handler.Equals(handler)))
        {
            return;
        }

        list.Add(new Subscription(topicPattern, handler, priority, BuildMatcher(topicPattern)));
        _resolved.Clear();
    }

    public void Unsubscribe(string topicPattern, Action<object> handler)
    {
        if (_subscriptions.TryGetValue(topicPattern, out List<Subscription>? list))
        {
            list.RemoveAll(s => s.Handler.Equals(handler));
            if (list.Count == 0)
            {
                _subscriptions.Remove(topicPattern);
            }

            _resolved.Clear();
        }
    }

    public bool HasSubscribers(string topic) => Resolve(topic).Length > 0;

    public void Publish(string topic, object message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(message);
        PublishedCount++;

        Subscription[] handlers = Resolve(topic);
        for (int i = 0; i < handlers.Length; i++)
        {
            Subscription subscription = handlers[i];
            try
            {
                subscription.Handler(message);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Handler for topic {Topic} threw while processing {MessageType}", topic, message.GetType().Name);
                _onHandlerError?.Invoke(e, subscription.Handler, message);
            }
        }
    }

    public void Register(string endpoint, Action<object> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(handler);
        _endpoints[endpoint] = handler;
    }

    public void Deregister(string endpoint) => _endpoints.Remove(endpoint);

    public bool IsRegistered(string endpoint) => _endpoints.ContainsKey(endpoint);

    public void Send(string endpoint, object message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_endpoints.TryGetValue(endpoint, out Action<object>? handler))
        {
            _log.LogError("Cannot send {MessageType}: endpoint {Endpoint} is not registered", message.GetType().Name, endpoint);
            return;
        }

        SentCount++;
        try
        {
            handler(message);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Endpoint {Endpoint} threw while processing {MessageType}", endpoint, message.GetType().Name);
            _onHandlerError?.Invoke(e, handler, message);
        }
    }

    public void Request(string endpoint, Request request, Action<Response> callback)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callback);
        _pending[request.Id] = callback;
        Send(endpoint, request);
    }

    public void Respond(Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (_pending.Remove(response.CorrelationId, out Action<Response>? callback))
        {
            try
            {
                callback(response);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Response callback threw for correlation {CorrelationId}", response.CorrelationId);
                _onHandlerError?.Invoke(e, callback, response);
            }
        }
        else
        {
            _log.LogWarning("No pending request for correlation {CorrelationId}", response.CorrelationId);
        }
    }

    private Subscription[] Resolve(string topic)
    {
        if (_resolved.TryGetValue(topic, out Subscription[]? cached))
        {
            return cached;
        }

        List<Subscription> matches = new();
        foreach (List<Subscription> list in _subscriptions.Values)
        {
            foreach (Subscription subscription in list)
            {
                if (subscription.Matcher(topic))
                {
                    matches.Add(subscription);
                }
            }
        }

        Subscription[] result = matches.OrderByDescending(s => s.Priority).ThenBy(s => s.Sequence).ToArray();
        _resolved[topic] = result;
        return result;
    }

    private static Func<string, bool> BuildMatcher(string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return topic => string.Equals(topic, pattern, StringComparison.Ordinal);
        }

        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        Regex compiled = new(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        return topic => compiled.IsMatch(topic);
    }

    private sealed class Subscription
    {
        private static long _counter;

        public Subscription(string pattern, Action<object> handler, int priority, Func<string, bool> matcher)
        {
            Pattern = pattern;
            Handler = handler;
            Priority = priority;
            Matcher = matcher;
            Sequence = Interlocked.Increment(ref _counter);
        }

        public string Pattern { get; }

        public Action<object> Handler { get; }

        public int Priority { get; }

        public Func<string, bool> Matcher { get; }

        public long Sequence { get; }
    }
}
