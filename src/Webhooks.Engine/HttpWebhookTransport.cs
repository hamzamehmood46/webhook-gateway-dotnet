namespace Webhooks.Engine;

/// <summary>Sends a webhook as an HTTP POST. A timeout or network error comes back as a null status, which is retried.</summary>
public sealed class HttpWebhookTransport : IWebhookTransport
{
    private readonly HttpClient _client;

    public HttpWebhookTransport(HttpClient client, TimeSpan? timeout = null)
    {
        _client = client;
        _client.Timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<TransportResult> SendAsync(WebhookEndpoint endpoint, WebhookRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = new StringContent(request.Body, System.Text.Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var response = await _client.SendAsync(message, ct);
            return new TransportResult((int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new TransportResult(null, ex.Message);
        }
    }
}
