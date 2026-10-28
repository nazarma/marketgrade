using Microsoft.AspNetCore.SignalR;

namespace Marketgrade.Backend;

/// <summary>
/// Live data for the user interface. The server pushes <c>status</c> (the full engine status) once per second
/// and <c>event</c> for every new event to every client.
/// </summary>
public sealed class LiveHub : Hub
{
    public const string Path = "/hubs/live";
}
