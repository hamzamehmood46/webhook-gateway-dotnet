using Webhooks.Engine;

namespace Webhooks.Tests;

/// <summary>Sender and receiver wired together, including the failure that makes idempotency necessary.</summary>
public class EndToEndTests
{
    private readonly FakeClock _clock = new();
    private readonly WebhookDispatcher _dispatcher;
    private readonly WebhookReceiver _receiver;
    private readonly LoopbackTransport _transport;

    public EndToEndTests()
    {
        WebhookDispatcher? dispatcher = null;
        _receiver = new WebhookReceiver(_clock, id => dispatcher!.SecretsFor(id));
        _transport = new LoopbackTransport(_receiver);
        _dispatcher = dispatcher = new WebhookDispatcher(_clock, _transport);
    }

    [Fact]
    public async Task A_published_event_is_delivered_signed_and_processed()
    {
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{\"orderId\":7}");

        await _dispatcher.ProcessDueAsync();

        var received = Assert.Single(_receiver.Received);
        Assert.Equal("order.created", received.EventType);
        Assert.Equal("{\"orderId\":7}", received.Body);
    }

    [Fact]
    public async Task A_lost_reply_makes_the_sender_retry_but_the_receiver_still_processes_the_event_once()
    {
        _transport.LoseTheFirstReply();
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");

        await _dispatcher.ProcessDueAsync();
        Assert.Single(_receiver.Received);
        Assert.Equal(DeliveryStatus.Pending, _dispatcher.Deliveries().Single().Status);

        _clock.Advance(TimeSpan.FromSeconds(10));
        await _dispatcher.ProcessDueAsync();

        var delivery = _dispatcher.Deliveries().Single();
        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(2, delivery.Attempts.Count);
        Assert.Single(_receiver.Received);
    }

    [Fact]
    public async Task A_secret_rotated_mid_flight_does_not_lose_the_event()
    {
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");
        _dispatcher.RotateSecret(endpoint.Id, TimeSpan.FromHours(1));

        await _dispatcher.ProcessDueAsync();

        Assert.Single(_receiver.Received);
    }

    [Fact]
    public async Task Once_the_grace_period_ends_a_request_signed_with_the_old_secret_is_refused()
    {
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        var old = endpoint.Secret;
        _dispatcher.RotateSecret(endpoint.Id, TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = _receiver.Receive(endpoint.Id, Make.Headers(old, "evt_x", _clock.GetUtcNow(), "{}"), "{}");

        Assert.Equal(401, result.StatusCode);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Many_events_to_many_endpoints_are_each_processed_exactly_once()
    {
        for (var i = 0; i < 3; i++)
        {
            _dispatcher.AddEndpoint($"demo://{i}");
        }

        for (var i = 0; i < 20; i++)
        {
            _dispatcher.Publish("order.created", $"{{\"n\":{i}}}");
        }

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _dispatcher.ProcessDueAsync()));

        Assert.Equal(60, _receiver.Received.Count);
        Assert.Equal(60, _receiver.Received.Select(r => (r.EndpointId, r.EventId)).Distinct().Count());
    }
}
