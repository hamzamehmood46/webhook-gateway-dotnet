# Webhook Gateway: send and receive webhooks that survive real networks

[![CI](https://github.com/hamzamehmood46/webhook-gateway-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/hamzamehmood46/webhook-gateway-dotnet/actions/workflows/ci.yml)

**Live demo: [webhook-gateway-dotnet.onrender.com/swagger](https://webhook-gateway-dotnet.onrender.com/swagger)** (Swagger UI). Hosted on Render's free tier, so the first load after idle can take about a minute, and demo data resets on restart.

Webhooks look simple and fail in four ways: the receiver is down, the reply gets lost after the work was done, someone forges or replays a request, and a secret has to change without dropping events. This project is both halves of that problem in **.NET 8**: a sender with retries and a dead-letter queue, and a receiver that verifies signatures and processes each event exactly once.

## Try it (3 minutes)

The demo never calls outside servers (that would make it a free request-forwarding tool for strangers), so an endpoint's url names a simulated receiver instead: `demo://healthy`, `demo://flaky` (fails twice, then recovers), `demo://lost-ack` (does the work but the reply is lost), `demo://down`, `demo://gone` (410) and `demo://bad-request` (400).

1. `POST /api/endpoints` with `{"url": "demo://flaky"}`. Note the secret in the response.
2. `POST /api/events` with `{"type": "order.created", "payload": {"orderId": 1}}`. A delivery is queued.
3. `GET /api/deliveries`: it is `Pending` after a failed first attempt, with the retry time shown.
4. `POST /api/demo/skip` with `{"seconds": 60}` to skip ahead past a retry delay instead of waiting, then `GET /api/deliveries` (the second attempt fails too). Skip another 60 seconds and check again: it succeeded on the third attempt.
5. Create a `demo://lost-ack` endpoint and publish. The receiver processes the event, the reply is "lost", the sender retries, and `GET /api/received` still shows the event **once**.
6. Create a `demo://down` endpoint and publish. Skip ahead a few times (`{"seconds": 7200}`): the delivery ends `DeadLettered` after 6 attempts. Then `POST /api/deliveries/{id}/redeliver`.
7. Try the receiver directly: `POST /api/endpoints/{id}/sign` returns the headers and body to send to `POST /hooks/{id}`. Send them twice (second is acknowledged as a duplicate), then change one character of the body (401).

## How it works

| Problem | What the code does |
|---|---|
| **Forged or tampered request** | Each request is signed with HMAC-SHA256 over `{eventId}.{timestamp}.{body}`. The receiver recomputes it and compares in constant time. |
| **Replay of a captured request** | The timestamp is signed, so it cannot be edited, and a genuine request older (or further in the future) than 5 minutes is refused. The event id is signed too, so a captured request cannot be re-sent as a new event. |
| **Receiver down** | Retries after 10s, 30s, 2m, 10m and 1h (6 attempts), then the delivery moves to a dead-letter state. Each attempt is signed with the current time so a late retry is not rejected as a replay. |
| **Permanent failures** | A 4xx (except 429) will not succeed on retry, so it is dead-lettered immediately. A 410 Gone also disables the endpoint. Timeouts, 429 and 5xx are retried. |
| **Lost reply** | The sender cannot tell "never arrived" from "arrived but the reply was lost", so it retries with the **same event id**. The receiver remembers ids and acknowledges a duplicate without processing it again. |
| **Dead letters** | Kept with their attempt history and the reason, and can be redelivered by hand (same event id). |
| **Secret rotation** | A new secret is issued and the old one stays accepted for a grace period, so events signed just before the switch are not rejected. |
| **Several workers** | A delivery is claimed under a lease before it is sent, so workers never send the same delivery twice. If a worker dies, the lease expires and another picks it up; a late result from the dead worker is ignored. |

## How it is built

```
src/Webhooks.Engine   C#: signing, receiver, dispatcher, HTTP transport. No web code, injected clock.
src/Webhooks.Api      ASP.NET Core minimal API: the demo, the real /hooks receiver, a background worker
tests/                xUnit tests
```

The engine takes the transport as an interface. `HttpWebhookTransport` sends real HTTP POSTs and is tested against a stub handler; the live demo uses simulated receivers instead.

## Tests

```bash
dotnet test
```

68 tests, including:

- signatures: tampered body, changed event id, wrong secret, rewritten timestamp, genuine-but-old request, the exact edge of the tolerance window, missing and malformed headers, several signatures in one header;
- the receiver: processes once, acknowledges duplicates, 50 parallel copies of one event are processed exactly once, bad requests are rejected without using up the event id;
- the dispatcher: the exact retry schedule and that nothing is sent early, give-up after the last attempt, every status class, 410 handling, redelivery, a fresh valid signature on every attempt with the same event id;
- concurrency: four workers over 25 deliveries send each exactly once; an abandoned delivery is picked up after its lease, and the dead worker's late failure cannot overwrite the result;
- end to end: a lost reply produces a retry but one processed event, and secret rotation mid-flight loses nothing.

Writing the crash-recovery test exposed a real bug: a worker coming back late could overwrite a delivery another worker had completed. It is fixed with a claim token.

I also broke the rules on purpose to check the tests notice: not signing the event id, removing the replay window, removing de-duplication, retrying client errors, removing the stale-worker guard, ignoring 410, and removing the backoff. Each made tests fail.

## Run it locally

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Webhooks.Api
```

## Scope, honestly

- State is in memory, so it resets on restart. A production version would persist endpoints and deliveries (see the [Transactional Outbox](https://github.com/hamzamehmood46/outbox-pattern-dotnet) project for the pattern).
- Delivery order is not guaranteed, as with most webhook providers; receivers should use the event id and not rely on order.
- The live demo uses simulated receivers only. Sending to arbitrary urls would also need protection against requests to internal addresses.

## Related projects

- [Transactional Outbox and Idempotent Inbox](https://github.com/hamzamehmood46/outbox-pattern-dotnet)
- [OrderFlow](https://github.com/hamzamehmood46/orderflow-dotnet)
- [Resilience Toolkit](https://github.com/hamzamehmood46/resilience-toolkit-dotnet)

## License

MIT
