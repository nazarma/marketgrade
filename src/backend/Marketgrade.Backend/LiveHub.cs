using Microsoft.AspNetCore.SignalR;

namespace Marketgrade.Backend;

/// <summary>
/// Live data for the user interface. The server pushes <c>status</c> (the full engine status) once per second
/// and <c>event</c> for every new event to every client, and <c>flow</c> every 100 ms only to clients that
/// called <see cref="SubscribeFlow"/>.
/// </summary>
public sealed class LiveHub(FlowSubscriptions flow) : Hub
{
    public const string Path = "/hubs/live";

    public Task SubscribeFlow() => flow.AddAsync(Context.ConnectionId, Groups);

    public Task UnsubscribeFlow() => flow.RemoveAsync(Context.ConnectionId, Groups);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR takes a closed connection out of its groups by itself.
        flow.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
