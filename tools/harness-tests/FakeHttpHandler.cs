#nullable enable

using System.Net.Http;

public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    public FakeHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        _send = send;
    }

    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));
        return await _send(request, cancellationToken);
    }
}
