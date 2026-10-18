using System.Text.Json;
using Microsoft.OpenApi.Models;
using Webhooks.Api;
using Webhooks.Engine;

var builder = WebApplication.CreateBuilder(args);

var clock = new SkewedClock();
var options = new WebhookOptions();
WebhookDispatcher? dispatcher = null;
WebhookReceiver? receiver = null;
var transport = new DemoTransport(() => receiver!);
dispatcher = new WebhookDispatcher(clock, transport, options);
receiver = new WebhookReceiver(clock, id => dispatcher.SecretsFor(id), options);

builder.Services.AddSingleton(clock);
builder.Services.AddSingleton(dispatcher);
builder.Services.AddSingleton(receiver);
if (builder.Configuration.GetValue("Worker:Enabled", true))
{
    builder.Services.AddHostedService<DeliveryWorker>();
}

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "Webhook Gateway",
    Version = "v1",
    Description = "Send signed webhooks with retries and a dead-letter queue, and receive them with replay protection and de-duplication. Endpoint urls are demo://healthy, flaky, lost-ack, down, gone or bad-request.",
}));

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

var api = app.MapGroup("/api");

// ---- Sender: endpoints, events and deliveries ----
api.MapPost("/endpoints", (NewEndpoint body, WebhookDispatcher d) =>
{
    if (DemoTransport.BehaviorOf(body.Url ?? "") is null)
    {
        return Results.BadRequest(new { error = $"This demo only sends to simulated receivers: {string.Join(", ", DemoTransport.Behaviors.Select(b => "demo://" + b))}." });
    }

    var endpoint = d.AddEndpoint(body.Url!, body.EventTypes);
    return Results.Created($"/api/endpoints/{endpoint.Id}", new { endpoint.Id, endpoint.Url, endpoint.EventTypes, secret = endpoint.Secret, note = "In a real service the secret is shown once." });
});

api.MapGet("/endpoints", (WebhookDispatcher d) =>
    d.Endpoints().Select(e => new { e.Id, e.Url, e.EventTypes, e.Enabled, e.DisabledReason }));

api.MapDelete("/endpoints/{id:guid}", (Guid id, WebhookDispatcher d) => d.RemoveEndpoint(id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/endpoints/{id:guid}/rotate-secret", (Guid id, RotateRequest? body, WebhookDispatcher d) =>
    d.RotateSecret(id, TimeSpan.FromMinutes(body?.GraceMinutes ?? 60)) is { } secret
        ? Results.Ok(new { secret, note = "The previous secret stays valid for the grace period." })
        : Results.NotFound());

api.MapPost("/events", (NewEvent body, WebhookDispatcher d) =>
{
    if (string.IsNullOrWhiteSpace(body.Type))
    {
        return Results.BadRequest(new { error = "An event needs a type." });
    }

    var deliveries = d.Publish(body.Type, body.Payload?.GetRawText() ?? "{}");
    return Results.Accepted(value: new { queued = deliveries.Count, deliveries });
});

api.MapGet("/deliveries", (WebhookDispatcher d) => d.Deliveries());

api.MapGet("/deliveries/{id:guid}", (Guid id, WebhookDispatcher d) => d.GetDelivery(id) is { } view ? Results.Ok(view) : Results.NotFound());

api.MapPost("/deliveries/{id:guid}/redeliver", (Guid id, WebhookDispatcher d) =>
    d.Redeliver(id) ? Results.Accepted() : Results.Conflict(new { error = "Only dead-lettered deliveries can be redelivered." }));

api.MapPost("/worker/run", async (WebhookDispatcher d) => new { processed = await d.ProcessDueAsync() });

// ---- Demo helpers ----
api.MapPost("/demo/skip", (SkipRequest body, SkewedClock c) =>
{
    var seconds = Math.Clamp(body.Seconds, 0, 7 * 24 * 3600);
    return Results.Ok(new { now = c.Skip(TimeSpan.FromSeconds(seconds)) });
});

api.MapPost("/endpoints/{id:guid}/sign", (Guid id, SignRequest body, WebhookDispatcher d, SkewedClock c) =>
{
    var endpoint = d.Endpoint(id);
    if (endpoint is null)
    {
        return Results.NotFound();
    }

    var eventId = string.IsNullOrWhiteSpace(body.EventId) ? "evt_" + Guid.NewGuid().ToString("N") : body.EventId;
    var text = body.Body ?? "{}";
    return Results.Ok(new
    {
        postTo = $"/hooks/{id}",
        headers = new Dictionary<string, string>
        {
            [Headers.Id] = eventId,
            [Headers.Event] = body.Type ?? "test.event",
            [Headers.Signature] = Signature.Sign(endpoint.Secret, eventId, c.GetUtcNow().ToUnixTimeSeconds(), text),
        },
        body = text,
    });
});

// ---- Receiver: a real HTTP endpoint that verifies, de-duplicates and logs ----
app.MapPost("/hooks/{endpointId:guid}", async (Guid endpointId, HttpRequest request, WebhookReceiver r) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
    var result = r.Receive(endpointId, headers, body);
    return Results.Json(new { outcome = result.Outcome.ToString(), result.Message }, statusCode: result.StatusCode);
});

api.MapGet("/received", (WebhookReceiver r) => r.Received);

app.Run();

public record NewEndpoint(string? Url, string[]? EventTypes);
public record RotateRequest(int? GraceMinutes);
public record NewEvent(string? Type, JsonElement? Payload);
public record SkipRequest(int Seconds);
public record SignRequest(string? EventId, string? Type, string? Body);

public partial class Program;
