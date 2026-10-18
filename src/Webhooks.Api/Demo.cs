using Webhooks.Engine;

namespace Webhooks.Api;

/// <summary>The real clock plus an offset, so a visitor can skip ahead past a retry delay instead of waiting an hour.</summary>
public sealed class SkewedClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public DateTimeOffset Skip(TimeSpan by)
    {
        Interlocked.Add(ref _offsetTicks, by.Ticks);
        return GetUtcNow();
    }
}

/// <summary>
/// Stands in for other people's servers. The demo never makes outbound HTTP calls (that would be a free
/// request-forwarding tool for strangers), so each endpoint url names a behavior instead:
/// healthy, flaky (fails twice then recovers), lost-ack (processes the event but the reply is lost),
/// down (always 503), gone (410) and bad-request (400).
/// </summary>
public sealed class DemoTransport : IWebhookTransport
{
    public static readonly string[] Behaviors = ["healthy", "flaky", "lost-ack", "down", "gone", "bad-request"];

    private readonly Func<WebhookReceiver> _receiver;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid, string), int> _attempts = [];

    public DemoTransport(Func<WebhookReceiver> receiver) => _receiver = receiver;

    public static string? BehaviorOf(string url) =>
        url.StartsWith("demo://", StringComparison.OrdinalIgnoreCase) && Behaviors.Contains(url[7..].ToLowerInvariant()) ? url[7..].ToLowerInvariant() : null;

    public Task<TransportResult> SendAsync(WebhookEndpoint endpoint, WebhookRequest request, CancellationToken ct)
    {
        int attempt;
        lock (_gate)
        {
            _attempts[(endpoint.Id, request.EventId)] = attempt = _attempts.GetValueOrDefault((endpoint.Id, request.EventId)) + 1;
        }

        TransportResult Deliver() => new(_receiver().Receive(endpoint.Id, request.Headers, request.Body).StatusCode);

        var result = BehaviorOf(endpoint.Url) switch
        {
            "healthy" => Deliver(),
            "flaky" => attempt <= 2 ? new TransportResult(503) : Deliver(),
            "lost-ack" => attempt == 1 ? LoseReply(Deliver()) : Deliver(),
            "down" => new TransportResult(503),
            "gone" => new TransportResult(410),
            "bad-request" => new TransportResult(400),
            _ => new TransportResult(null, "Unknown demo behavior."),
        };
        return Task.FromResult(result);
    }

    // The receiver did its work, but the sender never hears back.
    private static TransportResult LoseReply(TransportResult _) => new(null, "Timed out waiting for the reply.");
}

/// <summary>Runs the delivery worker in the background, once a second.</summary>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly WebhookDispatcher _dispatcher;

    public DeliveryWorker(WebhookDispatcher dispatcher) => _dispatcher = dispatcher;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _dispatcher.ProcessDueAsync(stoppingToken);
        }
    }
}
