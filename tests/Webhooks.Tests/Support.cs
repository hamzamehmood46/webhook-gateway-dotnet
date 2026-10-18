using Webhooks.Engine;

namespace Webhooks.Tests;

public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Answers each send from a script (the last answer repeats) and remembers what it was sent.</summary>
public sealed class ScriptedTransport : IWebhookTransport
{
    private readonly Queue<TransportResult> _script = new();
    private readonly object _gate = new();
    private TransportResult _last = new(200);

    public List<WebhookRequest> Sent { get; } = [];

    public ScriptedTransport Then(params int?[] statuses)
    {
        foreach (var status in statuses)
        {
            _script.Enqueue(new TransportResult(status, status is null ? "timed out" : null));
        }

        return this;
    }

    public Task<TransportResult> SendAsync(WebhookEndpoint endpoint, WebhookRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            Sent.Add(request);
            _last = _script.Count > 0 ? _script.Dequeue() : _last;
            return Task.FromResult(_last);
        }
    }
}

/// <summary>Delivers straight into a receiver, like a real network would.</summary>
public sealed class LoopbackTransport : IWebhookTransport
{
    private readonly WebhookReceiver _receiver;
    private readonly HashSet<string> _loseFirstReplyFor = [];
    private readonly HashSet<string> _alreadyLost = [];

    public LoopbackTransport(WebhookReceiver receiver) => _receiver = receiver;

    public LoopbackTransport LoseTheFirstReply() { _loseFirstReplyFor.Add("*"); return this; }

    public Task<TransportResult> SendAsync(WebhookEndpoint endpoint, WebhookRequest request, CancellationToken ct)
    {
        var result = _receiver.Receive(endpoint.Id, request.Headers, request.Body);
        if (_loseFirstReplyFor.Contains("*") && _alreadyLost.Add(request.EventId))
        {
            // The receiver processed it, but the reply never reached the sender.
            return Task.FromResult(new TransportResult(null, "timed out waiting for the reply"));
        }

        return Task.FromResult(new TransportResult(result.StatusCode));
    }
}

public static class Make
{
    public static Dictionary<string, string> Headers(string secret, string id, DateTimeOffset at, string body, string type = "order.created") => new()
    {
        [Webhooks.Engine.Headers.Id] = id,
        [Webhooks.Engine.Headers.Event] = type,
        [Webhooks.Engine.Headers.Signature] = Signature.Sign(secret, id, at.ToUnixTimeSeconds(), body),
    };
}
