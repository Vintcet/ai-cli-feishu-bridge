using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace AiCliFeishu.Bridge.Adapters.OpenCode;

public sealed class HttpOpenCodeEventSource(HttpClient httpClient) : IOpenCodeEventSource
{
    public async IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
        OpenCodeEndpoint endpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildEventUri(endpoint));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var parser = new OpenCodeSseParser();
        var buffer = new char[4_096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                break;
            }
            foreach (var rawEvent in parser.Feed(new string(buffer, 0, count)))
            {
                yield return rawEvent;
            }
        }
        foreach (var rawEvent in parser.Complete())
        {
            yield return rawEvent;
        }
    }

    private static Uri BuildEventUri(OpenCodeEndpoint endpoint)
    {
        var builder = new UriBuilder(new Uri(endpoint.BaseUri, "/event"));
        if (!string.IsNullOrWhiteSpace(endpoint.Directory))
        {
            builder.Query = $"directory={Uri.EscapeDataString(endpoint.Directory)}";
        }
        return builder.Uri;
    }
}
