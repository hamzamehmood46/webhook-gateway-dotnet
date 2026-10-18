namespace Webhooks.Engine;

public sealed class WebhookOptions
{
    /// <summary>Wait before each retry. Its length plus one is the most attempts a delivery gets.</summary>
    public IReadOnlyList<TimeSpan> RetrySchedule { get; init; } =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)];

    /// <summary>How far a signed timestamp may be from the receiver's clock before it counts as a replay.</summary>
    public TimeSpan SignatureTolerance { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a worker may hold a delivery before another worker may take it (crash recovery).</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long the receiver remembers event ids to recognise duplicates.</summary>
    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromHours(24);
}

public sealed class WebhookEndpoint
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Url { get; init; } = "";

    /// <summary>Event types this endpoint wants. Empty means all of them.</summary>
    public IReadOnlyList<string> EventTypes { get; init; } = [];

    public string Secret { get; internal set; } = "";
    public string? PreviousSecret { get; internal set; }
    public DateTimeOffset? PreviousSecretUntil { get; internal set; }
    public bool Enabled { get; internal set; } = true;
    public string? DisabledReason { get; internal set; }
}

public enum DeliveryStatus
{
    Pending,
    InFlight,
    Succeeded,
    DeadLettered,
}

public sealed record Attempt(int Number, DateTimeOffset At, int? StatusCode, string? Error, string Outcome);

public sealed class Delivery
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid EndpointId { get; init; }
    public string EventId { get; init; } = "";
    public string EventType { get; init; } = "";
    public string Body { get; init; } = "";
    public DeliveryStatus Status { get; internal set; } = DeliveryStatus.Pending;
    public DateTimeOffset NextAttemptAt { get; internal set; }
    public DateTimeOffset? LeaseUntil { get; internal set; }

    /// <summary>Identifies the worker currently holding the delivery, so a stale worker cannot overwrite a newer result.</summary>
    internal Guid ClaimId { get; set; }

    public int FailuresThisRound { get; internal set; }
    public string? DeadLetterReason { get; internal set; }
    public List<Attempt> Attempts { get; } = [];
}

public sealed record DeliveryView(
    Guid Id, Guid EndpointId, string EventId, string EventType, DeliveryStatus Status,
    DateTimeOffset NextAttemptAt, string? DeadLetterReason, IReadOnlyList<Attempt> Attempts);

public sealed record WebhookRequest(string EventId, string EventType, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>What came back from one send: an HTTP status, or null with an error for a timeout or network failure.</summary>
public sealed record TransportResult(int? StatusCode, string? Error = null);

public interface IWebhookTransport
{
    Task<TransportResult> SendAsync(WebhookEndpoint endpoint, WebhookRequest request, CancellationToken ct);
}

public static class Headers
{
    public const string Id = "Webhook-Id";
    public const string Event = "Webhook-Event";
    public const string Signature = "Webhook-Signature";
}
