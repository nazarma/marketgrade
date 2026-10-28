using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Marketgrade.Backend.Tests;

/// <summary>
/// A live hub client that connects as the user interface does: WebSockets only, no negotiation, and the token
/// in the query string, which is how the browser's client sends it (the .NET client would use a header).
/// </summary>
internal static class LiveHubClient
{
    public static HubConnection Create(BackendFactory factory, string token = BackendFactory.Token)
    {
        var server = factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, LiveHub.Path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri) { Query = $"access_token={Uri.EscapeDataString(token)}" }.Uri;
                    return await server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);
                };
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
    }
}
