using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Tests;

/// <summary>In-process WebSocket server that plays an exchange. Each test scripts what a connection does.</summary>
internal sealed class FakeExchangeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<long> _attempts = new();
    private readonly ConcurrentQueue<FakeConnection> _connections = new();
    private Func<FakeConnection, Task> _handler = connection => connection.Closed;

    private FakeExchangeServer(WebApplication app) => _app = app;

    public Uri Uri { get; private set; } = null!;

    /// <summary>When set, the WebSocket handshake is answered with this HTTP status instead.</summary>
    public int? RejectWithStatus { get; set; }

    /// <summary><see cref="Stopwatch"/> timestamps of every handshake attempt, accepted or not.</summary>
    public IReadOnlyList<long> Attempts => [.. _attempts];

    public IReadOnlyList<FakeConnection> Connections => [.. _connections];

    public static async Task<FakeExchangeServer> StartAsync(Func<FakeConnection, Task>? handler = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeExchangeServer(app);
        if (handler is not null)
        {
            server._handler = handler;
        }

        app.UseWebSockets();
        app.Map("/ws", server.HandleAsync);
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        server.Uri = new Uri($"ws://127.0.0.1:{port}/ws");
        return server;
    }

    public async Task WaitForConnectionsAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_connections.Count < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public async Task WaitForAttemptsAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_attempts.Count < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        _attempts.Enqueue(Stopwatch.GetTimestamp());
        if (RejectWithStatus is { } status || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = RejectWithStatus ?? StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new FakeConnection(socket, _connections.Count);
        _connections.Enqueue(connection);
        await _handler(connection);
        await connection.Closed;
    }
}

internal sealed class FakeConnection
{
    private readonly WebSocket _socket;
    private readonly ConcurrentQueue<string> _received = new();

    public FakeConnection(WebSocket socket, int index)
    {
        _socket = socket;
        Index = index;
        Closed = ReceiveAsync();
    }

    public int Index { get; }

    /// <summary>Completes when the client closes or drops the connection.</summary>
    public Task Closed { get; }

    public IReadOnlyList<string> Received => [.. _received];

    public Task SendAsync(string text) => SendAsync(Encoding.UTF8.GetBytes(text));

    public async Task SendAsync(byte[] message) =>
        await _socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    public async Task SendFragmentsAsync(params string[] fragments)
    {
        for (var i = 0; i < fragments.Length; i++)
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(fragments[i]), WebSocketMessageType.Text, i == fragments.Length - 1, CancellationToken.None);
            await Task.Delay(5);
        }
    }

    public Task CloseAsync() =>
        _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

    public async Task WaitForReceivedAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_received.Count < count)
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var length = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), CancellationToken.None);
                    length += result.Count;
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                _received.Enqueue(Encoding.UTF8.GetString(buffer, 0, length));
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
        }
    }
}
