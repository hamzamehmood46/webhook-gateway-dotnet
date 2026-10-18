namespace Webhooks.Engine;

/// <summary>
/// The sending side: turns each published event into one delivery per subscribed endpoint and keeps trying until
/// the endpoint accepts it or the attempts run out. A delivery keeps the same event id across every retry, so a
/// receiver can tell a retry from a new event. Retries use exponential-style delays; permanent failures and
/// exhausted deliveries go to a dead-letter state that can be redelivered by hand.
/// </summary>
public sealed class WebhookDispatcher
{
    private readonly TimeProvider _time;
    private readonly IWebhookTransport _transport;
    private readonly WebhookOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WebhookEndpoint> _endpoints = [];
    private readonly List<Delivery> _deliveries = [];

    public WebhookDispatcher(TimeProvider time, IWebhookTransport transport, WebhookOptions? options = null)
    {
        _time = time;
        _transport = transport;
        _options = options ?? new WebhookOptions();
    }

    public WebhookEndpoint AddEndpoint(string url, IReadOnlyList<string>? eventTypes = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("An endpoint needs a url.", nameof(url));
        }

        var endpoint = new WebhookEndpoint { Url = url, EventTypes = eventTypes ?? [], Secret = Signature.NewSecret() };
        lock (_gate)
        {
            _endpoints[endpoint.Id] = endpoint;
        }

        return endpoint;
    }

    public IReadOnlyList<WebhookEndpoint> Endpoints()
    {
        lock (_gate)
        {
            return _endpoints.Values.ToList();
        }
    }

    public WebhookEndpoint? Endpoint(Guid id)
    {
        lock (_gate)
        {
            return _endpoints.GetValueOrDefault(id);
        }
    }

    public bool RemoveEndpoint(Guid id)
    {
        lock (_gate)
        {
            return _endpoints.Remove(id);
        }
    }

    /// <summary>
    /// Issues a new signing secret. For the grace period the old one is still accepted by <see cref="SecretsFor"/>,
    /// so events signed before the receiver switched keys are not rejected.
    /// </summary>
    public string? RotateSecret(Guid id, TimeSpan grace)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(id, out var endpoint))
            {
                return null;
            }

            endpoint.PreviousSecret = endpoint.Secret;
            endpoint.PreviousSecretUntil = _time.GetUtcNow() + grace;
            endpoint.Secret = Signature.NewSecret();
            return endpoint.Secret;
        }
    }

    /// <summary>The secrets a receiver should currently accept for an endpoint, or null if it does not exist.</summary>
    public IReadOnlyList<string>? SecretsFor(Guid id)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(id, out var endpoint))
            {
                return null;
            }

            var secrets = new List<string> { endpoint.Secret };
            if (endpoint.PreviousSecret is not null && endpoint.PreviousSecretUntil > _time.GetUtcNow())
            {
                secrets.Add(endpoint.PreviousSecret);
            }

            return secrets;
        }
    }

    public void SetEnabled(Guid id, bool enabled)
    {
        lock (_gate)
        {
            if (_endpoints.TryGetValue(id, out var endpoint))
            {
                endpoint.Enabled = enabled;
                endpoint.DisabledReason = enabled ? null : "Disabled by hand.";
            }
        }
    }

    /// <summary>Queues the event for every enabled endpoint that wants this event type.</summary>
    public IReadOnlyList<DeliveryView> Publish(string eventType, string body)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("An event needs a type.", nameof(eventType));
        }

        var eventId = "evt_" + Guid.NewGuid().ToString("N");
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var created = new List<DeliveryView>();
            foreach (var endpoint in _endpoints.Values.Where(e => e.Enabled && (e.EventTypes.Count == 0 || e.EventTypes.Contains(eventType))))
            {
                var delivery = new Delivery { EndpointId = endpoint.Id, EventId = eventId, EventType = eventType, Body = body, NextAttemptAt = now };
                _deliveries.Add(delivery);
                created.Add(View(delivery));
            }

            return created;
        }
    }

    public IReadOnlyList<DeliveryView> Deliveries()
    {
        lock (_gate)
        {
            return _deliveries.Select(View).ToList();
        }
    }

    public DeliveryView? GetDelivery(Guid id)
    {
        lock (_gate)
        {
            return _deliveries.Where(d => d.Id == id).Select(View).FirstOrDefault();
        }
    }

    /// <summary>Puts a dead-lettered delivery back in the queue with a fresh set of attempts and the same event id.</summary>
    public bool Redeliver(Guid deliveryId)
    {
        lock (_gate)
        {
            var delivery = _deliveries.FirstOrDefault(d => d.Id == deliveryId);
            if (delivery is null || delivery.Status != DeliveryStatus.DeadLettered || !_endpoints.TryGetValue(delivery.EndpointId, out var endpoint))
            {
                return false;
            }

            endpoint.Enabled = true;
            endpoint.DisabledReason = null;
            delivery.Status = DeliveryStatus.Pending;
            delivery.FailuresThisRound = 0;
            delivery.DeadLetterReason = null;
            delivery.NextAttemptAt = _time.GetUtcNow();
            return true;
        }
    }

    /// <summary>Sends every delivery that is due. Safe to call from several workers at once: each delivery is claimed first.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var work = new List<(Delivery Delivery, WebhookEndpoint Endpoint, WebhookRequest Request, Guid Claim)>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var delivery in _deliveries)
            {
                var due = (delivery.Status == DeliveryStatus.Pending && delivery.NextAttemptAt <= now)
                    || (delivery.Status == DeliveryStatus.InFlight && delivery.LeaseUntil <= now);
                if (!due)
                {
                    continue;
                }

                if (!_endpoints.TryGetValue(delivery.EndpointId, out var endpoint) || !endpoint.Enabled)
                {
                    delivery.Status = DeliveryStatus.DeadLettered;
                    delivery.DeadLetterReason = endpoint is null ? "The endpoint was removed." : $"The endpoint is disabled ({endpoint.DisabledReason}).";
                    continue;
                }

                delivery.Status = DeliveryStatus.InFlight;
                delivery.LeaseUntil = now + _options.Lease;
                delivery.ClaimId = Guid.NewGuid();
                work.Add((delivery, endpoint, BuildRequest(delivery, endpoint, now), delivery.ClaimId));
            }
        }

        await Task.WhenAll(work.Select(w => SendOne(w.Delivery, w.Endpoint, w.Request, w.Claim, ct)));
        return work.Count;
    }

    // Each attempt is signed with the current time, so a retry hours later is not rejected as a replay.
    private static WebhookRequest BuildRequest(Delivery delivery, WebhookEndpoint endpoint, DateTimeOffset now) =>
        new(delivery.EventId, delivery.EventType, delivery.Body, new Dictionary<string, string>
        {
            [Headers.Id] = delivery.EventId,
            [Headers.Event] = delivery.EventType,
            [Headers.Signature] = Signature.Sign(endpoint.Secret, delivery.EventId, now.ToUnixTimeSeconds(), delivery.Body),
        });

    private async Task SendOne(Delivery delivery, WebhookEndpoint endpoint, WebhookRequest request, Guid claim, CancellationToken ct)
    {
        TransportResult result;
        try
        {
            result = await _transport.SendAsync(endpoint, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new TransportResult(null, ex.Message);
        }

        lock (_gate)
        {
            if (delivery.ClaimId != claim || delivery.Status != DeliveryStatus.InFlight)
            {
                // Another worker took this delivery over after the lease ran out; its result is the one that counts.
                return;
            }

            var now = _time.GetUtcNow();
            var number = delivery.Attempts.Count + 1;
            delivery.LeaseUntil = null;

            if (result.StatusCode is >= 200 and < 300)
            {
                delivery.Attempts.Add(new Attempt(number, now, result.StatusCode, null, "succeeded"));
                delivery.Status = DeliveryStatus.Succeeded;
                return;
            }

            var retryable = result.StatusCode is null or 429 or >= 500;
            if (result.StatusCode == 410)
            {
                endpoint.Enabled = false;
                endpoint.DisabledReason = "The receiver answered 410 Gone.";
                delivery.Attempts.Add(new Attempt(number, now, result.StatusCode, null, "gone"));
                Fail(delivery, "The receiver answered 410 Gone, so the endpoint was disabled.");
            }
            else if (!retryable)
            {
                delivery.Attempts.Add(new Attempt(number, now, result.StatusCode, null, "rejected"));
                Fail(delivery, $"The receiver answered {result.StatusCode}, which will not succeed on retry.");
            }
            else if (delivery.FailuresThisRound >= _options.RetrySchedule.Count)
            {
                delivery.Attempts.Add(new Attempt(number, now, result.StatusCode, result.Error, "failed"));
                Fail(delivery, $"Gave up after {delivery.FailuresThisRound + 1} attempts.");
            }
            else
            {
                delivery.Attempts.Add(new Attempt(number, now, result.StatusCode, result.Error, "will retry"));
                delivery.NextAttemptAt = now + _options.RetrySchedule[delivery.FailuresThisRound];
                delivery.FailuresThisRound++;
                delivery.Status = DeliveryStatus.Pending;
            }
        }
    }

    private static void Fail(Delivery delivery, string reason)
    {
        delivery.Status = DeliveryStatus.DeadLettered;
        delivery.DeadLetterReason = reason;
    }

    private static DeliveryView View(Delivery d) =>
        new(d.Id, d.EndpointId, d.EventId, d.EventType, d.Status, d.NextAttemptAt, d.DeadLetterReason, d.Attempts.ToList());
}
