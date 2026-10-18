using Webhooks.Engine;

namespace Webhooks.Tests;

public class ReceiverTests
{
    private readonly FakeClock _clock = new();
    private readonly Guid _endpoint = Guid.NewGuid();
    private readonly WebhookReceiver _receiver;
    private const string Secret = "whsec_test";
    private const string Body = "{\"orderId\":42}";

    public ReceiverTests()
    {
        _receiver = new WebhookReceiver(_clock, id => id == _endpoint ? [Secret] : null);
    }

    private ReceiveResult Send(string id = "evt_1", string body = Body, string secret = Secret) =>
        _receiver.Receive(_endpoint, Make.Headers(secret, id, _clock.GetUtcNow(), body), body);

    [Fact]
    public void A_valid_event_is_processed_once()
    {
        var result = Send();

        Assert.Equal(ReceiveOutcome.Processed, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.Single(_receiver.Received);
    }

    [Fact]
    public void The_same_event_arriving_again_is_acknowledged_but_not_processed_twice()
    {
        Send();

        var again = Send();

        Assert.Equal(ReceiveOutcome.Duplicate, again.Outcome);
        Assert.Equal(200, again.StatusCode);
        Assert.Single(_receiver.Received);
    }

    [Fact]
    public void Different_events_are_each_processed()
    {
        Send("evt_1");
        Send("evt_2");

        Assert.Equal(2, _receiver.Received.Count);
    }

    [Fact]
    public void A_bad_signature_is_rejected_with_401_and_nothing_is_processed()
    {
        var result = Send(secret: "whsec_wrong");

        Assert.Equal(ReceiveOutcome.Rejected, result.Outcome);
        Assert.Equal(401, result.StatusCode);
        Assert.Empty(_receiver.Received);
    }

    [Fact]
    public void A_rejected_event_does_not_use_up_its_id()
    {
        Send(secret: "whsec_wrong");

        Assert.Equal(ReceiveOutcome.Processed, Send().Outcome);
    }

    [Fact]
    public void A_captured_request_replayed_later_is_rejected_even_though_its_signature_is_genuine()
    {
        var headers = Make.Headers(Secret, "evt_1", _clock.GetUtcNow(), Body);
        _clock.Advance(TimeSpan.FromMinutes(10));

        var result = _receiver.Receive(_endpoint, headers, Body);

        Assert.Equal(VerifyResult.OutsideTolerance, result.Verification);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public void A_missing_event_id_is_a_400()
    {
        var headers = Make.Headers(Secret, "evt_1", _clock.GetUtcNow(), Body);
        headers.Remove(Headers.Id);

        var result = _receiver.Receive(_endpoint, headers, Body);

        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public void An_unknown_endpoint_is_a_404()
    {
        var result = _receiver.Receive(Guid.NewGuid(), Make.Headers(Secret, "evt_1", _clock.GetUtcNow(), Body), Body);

        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public void Header_names_are_not_case_sensitive()
    {
        var headers = Make.Headers(Secret, "evt_1", _clock.GetUtcNow(), Body)
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value);

        Assert.Equal(ReceiveOutcome.Processed, _receiver.Receive(_endpoint, headers, Body).Outcome);
    }

    [Fact]
    public void Event_ids_are_remembered_only_for_the_dedupe_window()
    {
        Send("evt_1");
        _clock.Advance(TimeSpan.FromHours(25));

        Assert.Equal(ReceiveOutcome.Processed, Send("evt_1").Outcome);
    }

    [Fact]
    public void Parallel_deliveries_of_one_event_are_processed_exactly_once()
    {
        var outcomes = Enumerable.Range(0, 50).AsParallel().Select(_ => Send("evt_race").Outcome).ToList();

        Assert.Equal(1, outcomes.Count(o => o == ReceiveOutcome.Processed));
        Assert.Equal(49, outcomes.Count(o => o == ReceiveOutcome.Duplicate));
        Assert.Single(_receiver.Received);
    }
}
