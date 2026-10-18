namespace Webhooks.Engine;

public enum ReceiveOutcome
{
    Processed,
    Duplicate,
    Rejected,
    UnknownEndpoint,
}

public sealed record ReceiveResult(ReceiveOutcome Outcome, VerifyResult? Verification = null, string Message = "")
{
    /// <summary>The status a receiver should answer with. A duplicate is a success so the sender stops retrying.</summary>
    public int StatusCode => Outcome switch
    {
        ReceiveOutcome.Processed or ReceiveOutcome.Duplicate => 200,
        ReceiveOutcome.UnknownEndpoint => 404,
        _ => Verification is VerifyResult.Mismatch or VerifyResult.OutsideTolerance ? 401 : 400,
    };
}

public sealed record ReceivedEvent(string EventId, Guid EndpointId, string EventType, string Body, DateTimeOffset At);

/// <summary>
/// The receiving side of a webhook: checks the signature and the timestamp, and processes each event id once.
/// The sender retries on any failure, including a lost reply, so the same event can arrive several times;
/// only the first one is processed and the rest are acknowledged and ignored.
/// </summary>
public sealed class WebhookReceiver
{
    private readonly TimeProvider _time;
    private readonly WebhookOptions _options;
    private readonly Func<Guid, IReadOnlyList<string>?> _secretsFor;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid, string), DateTimeOffset> _seen = [];
    private readonly List<ReceivedEvent> _log = [];

    public WebhookReceiver(TimeProvider time, Func<Guid, IReadOnlyList<string>?> secretsFor, WebhookOptions? options = null)
    {
        _time = time;
        _secretsFor = secretsFor;
        _options = options ?? new WebhookOptions();
    }

    public IReadOnlyList<ReceivedEvent> Received
    {
        get
        {
            lock (_gate)
            {
                return _log.ToList();
            }
        }
    }

    public ReceiveResult Receive(Guid endpointId, IReadOnlyDictionary<string, string> headers, string body)
    {
        var secrets = _secretsFor(endpointId);
        if (secrets is null)
        {
            return new ReceiveResult(ReceiveOutcome.UnknownEndpoint, Message: "No such endpoint.");
        }

        var lookup = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        lookup.TryGetValue(Headers.Id, out var eventId);
        lookup.TryGetValue(Headers.Signature, out var signature);
        lookup.TryGetValue(Headers.Event, out var eventType);
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return new ReceiveResult(ReceiveOutcome.Rejected, VerifyResult.Malformed, "Missing Webhook-Id header.");
        }

        var now = _time.GetUtcNow();
        var verdict = Signature.Verify(signature, eventId, body, secrets, now, _options.SignatureTolerance);
        if (verdict != VerifyResult.Valid)
        {
            return new ReceiveResult(ReceiveOutcome.Rejected, verdict, verdict switch
            {
                VerifyResult.Missing => "No Webhook-Signature header.",
                VerifyResult.Malformed => "The signature header is malformed.",
                VerifyResult.OutsideTolerance => "The timestamp is too old or too far in the future (possible replay).",
                _ => "The signature does not match.",
            });
        }

        lock (_gate)
        {
            foreach (var old in _seen.Where(s => now - s.Value > _options.DedupeWindow).Select(s => s.Key).ToList())
            {
                _seen.Remove(old);
            }

            if (!_seen.TryAdd((endpointId, eventId), now))
            {
                return new ReceiveResult(ReceiveOutcome.Duplicate, VerifyResult.Valid, "Already processed; acknowledged again.");
            }

            _log.Add(new ReceivedEvent(eventId, endpointId, eventType ?? "", body, now));
            return new ReceiveResult(ReceiveOutcome.Processed, VerifyResult.Valid, "Processed.");
        }
    }
}
