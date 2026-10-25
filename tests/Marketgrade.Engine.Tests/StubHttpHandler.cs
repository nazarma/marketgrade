namespace Marketgrade.Engine.Tests;

/// <summary>Answers every HTTP request with <paramref name="respond"/> and remembers the requests.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _requests;

    public StubHttpHandler(System.Net.HttpStatusCode status, byte[] body)
        : this((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) }))
    {
    }

    public Uri? LastUri { get; private set; }

    public int Requests => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUri = request.RequestUri;
        Interlocked.Increment(ref _requests);
        return respond(request, cancellationToken);
    }
}
