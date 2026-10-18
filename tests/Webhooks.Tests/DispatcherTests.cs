using System.Net;
using Webhooks.Engine;

namespace Webhooks.Tests;

public class DispatcherTests
{
    private readonly FakeClock _clock = new();
    private readonly ScriptedTransport _transport = new();
    private readonly WebhookDispatcher _dispatcher;

    public DispatcherTests()
    {
        _dispatcher = new WebhookDispatcher(_clock, _transport);
    }

    private DeliveryView Only() => _dispatcher.Deliveries().Single();

    [Fact]
    public async Task An_event_goes_to_every_subscribed_endpoint_and_only_those()
    {
        var all = _dispatcher.AddEndpoint("demo://a");
        var orders = _dispatcher.AddEndpoint("demo://b", ["order.created"]);
        var refunds = _dispatcher.AddEndpoint("demo://c", ["refund.created"]);

        var created = _dispatcher.Publish("order.created", "{}");

        Assert.Equal(new[] { all.Id, orders.Id }.Order(), created.Select(d => d.EndpointId).Order());
        Assert.DoesNotContain(created, d => d.EndpointId == refunds.Id);
        await _dispatcher.ProcessDueAsync();
        Assert.All(_dispatcher.Deliveries(), d => Assert.Equal(DeliveryStatus.Succeeded, d.Status));
    }

    [Fact]
    public async Task A_disabled_endpoint_receives_nothing()
    {
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        _dispatcher.SetEnabled(endpoint.Id, false);

        Assert.Empty(_dispatcher.Publish("order.created", "{}"));
        Assert.Equal(0, await _dispatcher.ProcessDueAsync());
    }

    [Fact]
    public async Task A_2xx_answer_completes_the_delivery_in_one_attempt()
    {
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");

        Assert.Equal(1, await _dispatcher.ProcessDueAsync());

        Assert.Equal(DeliveryStatus.Succeeded, Only().Status);
        Assert.Single(Only().Attempts);
    }

    [Fact]
    public async Task Failures_are_retried_after_exactly_the_scheduled_delays()
    {
        _transport.Then(503, 503, 503, 503, 503, 200);
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");
        var expectedWaits = new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1) };

        foreach (var wait in expectedWaits)
        {
            var before = _clock.GetUtcNow();
            await _dispatcher.ProcessDueAsync();
            Assert.Equal(before + wait, Only().NextAttemptAt);

            _clock.Advance(wait - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await _dispatcher.ProcessDueAsync());
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        await _dispatcher.ProcessDueAsync();
        Assert.Equal(DeliveryStatus.Succeeded, Only().Status);
        Assert.Equal(6, Only().Attempts.Count);
    }

    [Fact]
    public async Task A_delivery_that_keeps_failing_is_dead_lettered_after_the_last_attempt()
    {
        _transport.Then(503);
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");

        for (var i = 0; i < 10; i++)
        {
            await _dispatcher.ProcessDueAsync();
            _clock.Advance(TimeSpan.FromHours(2));
        }

        Assert.Equal(DeliveryStatus.DeadLettered, Only().Status);
        Assert.Equal(6, Only().Attempts.Count);
        Assert.Contains("6 attempts", Only().DeadLetterReason);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task A_client_error_will_not_succeed_on_retry_so_it_is_dead_lettered_at_once(int status)
    {
        _transport.Then(status);
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");

        await _dispatcher.ProcessDueAsync();

        Assert.Equal(DeliveryStatus.DeadLettered, Only().Status);
        Assert.Single(Only().Attempts);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(null)]
    public async Task Throttling_server_errors_and_timeouts_are_retried(int? status)
    {
        _transport.Then(status, 200);
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");

        await _dispatcher.ProcessDueAsync();
        Assert.Equal(DeliveryStatus.Pending, Only().Status);
        _clock.Advance(TimeSpan.FromSeconds(10));
        await _dispatcher.ProcessDueAsync();

        Assert.Equal(DeliveryStatus.Succeeded, Only().Status);
    }

    [Fact]
    public async Task A_410_Gone_disables_the_endpoint_so_nothing_more_is_sent_to_it()
    {
        _transport.Then((int)HttpStatusCode.Gone);
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");

        await _dispatcher.ProcessDueAsync();

        Assert.Equal(DeliveryStatus.DeadLettered, Only().Status);
        Assert.False(_dispatcher.Endpoint(endpoint.Id)!.Enabled);
        Assert.Empty(_dispatcher.Publish("order.created", "{}"));
    }

    [Fact]
    public async Task A_transport_that_throws_is_treated_as_a_failed_attempt()
    {
        var dispatcher = new WebhookDispatcher(_clock, new ThrowingTransport());
        dispatcher.AddEndpoint("demo://a");
        dispatcher.Publish("order.created", "{}");

        await dispatcher.ProcessDueAsync();

        var delivery = dispatcher.Deliveries().Single();
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Contains("boom", delivery.Attempts.Single().Error);
    }

    [Fact]
    public async Task Every_attempt_carries_the_same_event_id_but_a_fresh_valid_signature()
    {
        _transport.Then(503, 503, 200);
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{\"n\":1}");

        await _dispatcher.ProcessDueAsync();
        _clock.Advance(TimeSpan.FromSeconds(10));
        await _dispatcher.ProcessDueAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));
        await _dispatcher.ProcessDueAsync();

        Assert.Equal(3, _transport.Sent.Count);
        Assert.Single(_transport.Sent.Select(r => r.EventId).Distinct());
        var stamps = _transport.Sent.Select(r => r.Headers[Headers.Signature].Split(',')[0]).ToList();
        Assert.Equal(3, stamps.Distinct().Count());
        Assert.All(_transport.Sent, r => Assert.Equal(VerifyResult.Valid,
            Signature.Verify(r.Headers[Headers.Signature], r.EventId, r.Body, [endpoint.Secret], _clock.GetUtcNow(), TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public async Task A_dead_lettered_delivery_can_be_redelivered_with_the_same_event_id()
    {
        _transport.Then(500);
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");
        for (var i = 0; i < 8; i++)
        {
            await _dispatcher.ProcessDueAsync();
            _clock.Advance(TimeSpan.FromHours(2));
        }

        var eventId = Only().EventId;
        _transport.Then(200);
        Assert.True(_dispatcher.Redeliver(Only().Id));
        Assert.Equal(DeliveryStatus.Pending, Only().Status);
        await _dispatcher.ProcessDueAsync();

        Assert.Equal(DeliveryStatus.Succeeded, Only().Status);
        Assert.Equal(eventId, Only().EventId);
        Assert.Equal(7, Only().Attempts.Count);
    }

    [Fact]
    public async Task Only_dead_lettered_deliveries_can_be_redelivered()
    {
        _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");
        await _dispatcher.ProcessDueAsync();

        Assert.False(_dispatcher.Redeliver(Only().Id));
        Assert.False(_dispatcher.Redeliver(Guid.NewGuid()));
    }

    [Fact]
    public async Task Redelivering_after_a_410_turns_the_endpoint_back_on()
    {
        _transport.Then(410, 200);
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");
        await _dispatcher.ProcessDueAsync();

        _dispatcher.Redeliver(Only().Id);
        await _dispatcher.ProcessDueAsync();

        Assert.True(_dispatcher.Endpoint(endpoint.Id)!.Enabled);
        Assert.Equal(DeliveryStatus.Succeeded, Only().Status);
    }

    [Fact]
    public async Task A_delivery_whose_endpoint_was_disabled_meanwhile_is_dead_lettered_not_sent()
    {
        _transport.Then(503);
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        _dispatcher.Publish("order.created", "{}");
        await _dispatcher.ProcessDueAsync();
        _dispatcher.SetEnabled(endpoint.Id, false);
        _clock.Advance(TimeSpan.FromSeconds(10));

        await _dispatcher.ProcessDueAsync();

        Assert.Equal(DeliveryStatus.DeadLettered, Only().Status);
        Assert.Single(_transport.Sent);
    }

    [Fact]
    public async Task Two_workers_running_at_once_never_send_the_same_delivery_twice()
    {
        var slow = new SlowTransport();
        var dispatcher = new WebhookDispatcher(_clock, slow);
        dispatcher.AddEndpoint("demo://a");
        for (var i = 0; i < 25; i++)
        {
            dispatcher.Publish("order.created", $"{{\"n\":{i}}}");
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => dispatcher.ProcessDueAsync()));

        Assert.Equal(25, slow.Calls);
        Assert.All(dispatcher.Deliveries(), d => Assert.Equal(DeliveryStatus.Succeeded, d.Status));
    }

    [Fact]
    public async Task A_delivery_abandoned_by_a_crashed_worker_is_picked_up_after_the_lease_expires()
    {
        var gate = new TaskCompletionSource<TransportResult>();
        var hanging = new HangingTransport(gate.Task);
        var dispatcher = new WebhookDispatcher(_clock, hanging);
        dispatcher.AddEndpoint("demo://a");
        dispatcher.Publish("order.created", "{}");

        var crashed = dispatcher.ProcessDueAsync();
        Assert.Equal(DeliveryStatus.InFlight, dispatcher.Deliveries().Single().Status);
        Assert.Equal(0, await dispatcher.ProcessDueAsync());

        _clock.Advance(TimeSpan.FromMinutes(2));
        hanging.Next = Task.FromResult(new TransportResult(200));
        Assert.Equal(1, await dispatcher.ProcessDueAsync());

        Assert.Equal(DeliveryStatus.Succeeded, dispatcher.Deliveries().Single().Status);
        gate.SetResult(new TransportResult(null, "late"));
        await crashed;

        // The stale worker's late failure must not overwrite the result of the worker that took over.
        var delivery = dispatcher.Deliveries().Single();
        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Single(delivery.Attempts);
    }

    [Fact]
    public void Rotating_a_secret_keeps_the_old_one_accepted_only_for_the_grace_period()
    {
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        var original = endpoint.Secret;

        var fresh = _dispatcher.RotateSecret(endpoint.Id, TimeSpan.FromHours(1));

        Assert.NotEqual(original, fresh);
        Assert.Equal([fresh, original], _dispatcher.SecretsFor(endpoint.Id));
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal([fresh], _dispatcher.SecretsFor(endpoint.Id));
    }

    [Fact]
    public async Task After_a_rotation_new_deliveries_are_signed_with_the_new_secret()
    {
        var endpoint = _dispatcher.AddEndpoint("demo://a");
        var fresh = _dispatcher.RotateSecret(endpoint.Id, TimeSpan.FromHours(1))!;
        _dispatcher.Publish("order.created", "{}");

        await _dispatcher.ProcessDueAsync();

        var sent = _transport.Sent.Single();
        Assert.Equal(VerifyResult.Valid, Signature.Verify(sent.Headers[Headers.Signature], sent.EventId, sent.Body, [fresh], _clock.GetUtcNow(), TimeSpan.FromMinutes(5)));
    }

    private sealed class ThrowingTransport : IWebhookTransport
    {
        public Task<TransportResult> SendAsync(WebhookEndpoint endpoint, WebhookRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class SlowTransport : IWebhookTransport
    {
        private int _calls;
        public int Calls => _calls;

        public async Task<TransportResult> SendAsync(WebhookEndpoint endpoint, WebhookRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(20, ct);
            return new TransportResult(200);
        }
    }

    private sealed class HangingTransport : IWebhookTransport
    {
        public HangingTransport(Task<TransportResult> first) => Next = first;
        public Task<TransportResult> Next { get; set; }
        public Task<TransportResult> SendAsync(WebhookEndpoint endpoint, WebhookRequest request, CancellationToken ct) => Next;
    }
}
