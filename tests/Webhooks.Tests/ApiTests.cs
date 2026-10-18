using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Webhooks.Tests;

public class ApiTests : IClassFixture<ApiTests.App>
{
    public sealed class App : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Worker:Enabled", "false");
    }

    private readonly HttpClient _client;

    public ApiTests(App app) => _client = app.CreateClient();

    private async Task<(Guid Id, string Secret)> Endpoint(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/endpoints", new { url });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("secret").GetString()!);
    }

    private async Task<JsonElement> Publish(string type = "order.created")
    {
        var response = await _client.PostAsJsonAsync("/api/events", new { type, payload = new { orderId = 1 } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task Run() => (await _client.PostAsync("/api/worker/run", null)).EnsureSuccessStatusCode();

    private async Task Skip(int seconds) => (await _client.PostAsJsonAsync("/api/demo/skip", new { seconds })).EnsureSuccessStatusCode();

    private async Task<JsonElement> Delivery(Guid id) => await _client.GetFromJsonAsync<JsonElement>($"/api/deliveries/{id}");

    [Fact]
    public async Task Only_simulated_receivers_are_accepted_so_the_demo_cannot_be_used_to_call_other_servers()
    {
        var response = await _client.PostAsJsonAsync("/api/endpoints", new { url = "https://example.com/hook" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_healthy_receiver_gets_the_event_and_the_delivery_succeeds()
    {
        var (id, _) = await Endpoint("demo://healthy");
        var queued = await Publish();
        var delivery = queued.GetProperty("deliveries").EnumerateArray().Single(d => d.GetProperty("endpointId").GetGuid() == id).GetProperty("id").GetGuid();

        await Run();

        var view = await Delivery(delivery);
        Assert.Equal("Succeeded", view.GetProperty("status").GetString());
        var received = await _client.GetFromJsonAsync<List<JsonElement>>("/api/received");
        Assert.Contains(received!, r => r.GetProperty("endpointId").GetGuid() == id);
    }

    [Fact]
    public async Task A_flaky_receiver_fails_twice_then_succeeds_after_the_clock_skips_past_the_retry_delays()
    {
        var (id, _) = await Endpoint("demo://flaky");
        var delivery = (await Publish()).GetProperty("deliveries").EnumerateArray().Single(d => d.GetProperty("endpointId").GetGuid() == id).GetProperty("id").GetGuid();

        await Run();
        Assert.Equal("Pending", (await Delivery(delivery)).GetProperty("status").GetString());
        await Skip(15);
        await Run();
        await Skip(45);
        await Run();

        var view = await Delivery(delivery);
        Assert.Equal("Succeeded", view.GetProperty("status").GetString());
        Assert.Equal(3, view.GetProperty("attempts").GetArrayLength());
    }

    [Fact]
    public async Task A_lost_reply_causes_a_retry_and_the_receiver_still_logs_the_event_once()
    {
        var (id, _) = await Endpoint("demo://lost-ack");
        var delivery = (await Publish()).GetProperty("deliveries").EnumerateArray().Single(d => d.GetProperty("endpointId").GetGuid() == id).GetProperty("id").GetGuid();

        await Run();
        await Skip(15);
        await Run();

        var view = await Delivery(delivery);
        Assert.Equal("Succeeded", view.GetProperty("status").GetString());
        Assert.Equal(2, view.GetProperty("attempts").GetArrayLength());
        var eventId = view.GetProperty("eventId").GetString();
        var received = await _client.GetFromJsonAsync<List<JsonElement>>("/api/received");
        Assert.Single(received!, r => r.GetProperty("endpointId").GetGuid() == id && r.GetProperty("eventId").GetString() == eventId);
    }

    [Fact]
    public async Task A_down_receiver_ends_in_the_dead_letter_queue_and_can_be_redelivered_once_it_is_back()
    {
        var (id, _) = await Endpoint("demo://down");
        var delivery = (await Publish()).GetProperty("deliveries").EnumerateArray().Single(d => d.GetProperty("endpointId").GetGuid() == id).GetProperty("id").GetGuid();

        for (var i = 0; i < 8; i++)
        {
            await Run();
            await Skip(2 * 3600);
        }

        var dead = await Delivery(delivery);
        Assert.Equal("DeadLettered", dead.GetProperty("status").GetString());
        Assert.Equal(6, dead.GetProperty("attempts").GetArrayLength());
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync($"/api/deliveries/{delivery}/redeliver", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/deliveries/{Guid.NewGuid()}/redeliver", null)).StatusCode);
    }

    [Fact]
    public async Task The_real_receiver_accepts_a_correctly_signed_request_once_and_refuses_tampering()
    {
        var (id, _) = await Endpoint("demo://healthy");
        var signed = await (await _client.PostAsJsonAsync($"/api/endpoints/{id}/sign", new { body = "{\"hello\":true}" })).Content.ReadFromJsonAsync<JsonElement>();

        HttpRequestMessage Request(string body)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, signed.GetProperty("postTo").GetString()) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var header in signed.GetProperty("headers").EnumerateObject())
            {
                message.Headers.Add(header.Name, header.Value.GetString());
            }

            return message;
        }

        var first = await _client.SendAsync(Request("{\"hello\":true}"));
        var second = await _client.SendAsync(Request("{\"hello\":true}"));
        var tampered = await _client.SendAsync(Request("{\"hello\":false}"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("Processed", await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("Duplicate", await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
    }

    [Fact]
    public async Task Rotating_a_secret_returns_a_new_one()
    {
        var (id, old) = await Endpoint("demo://healthy");

        var response = await _client.PostAsJsonAsync($"/api/endpoints/{id}/rotate-secret", new { graceMinutes = 10 });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEqual(old, body.GetProperty("secret").GetString());
    }

    [Fact]
    public async Task An_event_needs_a_type()
    {
        var response = await _client.PostAsJsonAsync("/api/events", new { type = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
