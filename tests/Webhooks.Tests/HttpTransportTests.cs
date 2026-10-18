using Webhooks.Engine;

namespace Webhooks.Tests;

public class HttpTransportTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        public string? SeenBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request);
        }
    }

    private static readonly WebhookEndpoint Endpoint = new() { Url = "https://example.test/hook" };

    private static WebhookRequest Request() => new("evt_1", "order.created", "{\"a\":1}", new Dictionary<string, string>
    {
        [Headers.Id] = "evt_1",
        [Headers.Event] = "order.created",
        [Headers.Signature] = "t=1,v1=00",
    });

    [Fact]
    public async Task It_posts_the_body_and_the_webhook_headers_and_returns_the_status()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)));
        var transport = new HttpWebhookTransport(new HttpClient(handler));

        var result = await transport.SendAsync(Endpoint, Request(), default);

        Assert.Equal(204, result.StatusCode);
        Assert.Equal(HttpMethod.Post, handler.Seen!.Method);
        Assert.Equal("{\"a\":1}", handler.SeenBody);
        Assert.Equal("evt_1", handler.Seen.Headers.GetValues(Headers.Id).Single());
        Assert.Equal("t=1,v1=00", handler.Seen.Headers.GetValues(Headers.Signature).Single());
    }

    [Fact]
    public async Task A_network_failure_comes_back_as_a_null_status_so_it_is_retried()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var transport = new HttpWebhookTransport(new HttpClient(handler));

        var result = await transport.SendAsync(Endpoint, Request(), default);

        Assert.Null(result.StatusCode);
        Assert.Contains("connection refused", result.Error);
    }

    [Fact]
    public async Task A_timeout_comes_back_as_a_null_status()
    {
        var handler = new StubHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return new HttpResponseMessage();
        });
        var transport = new HttpWebhookTransport(new HttpClient(handler), TimeSpan.FromMilliseconds(100));

        var result = await transport.SendAsync(Endpoint, Request(), default);

        Assert.Null(result.StatusCode);
    }
}
