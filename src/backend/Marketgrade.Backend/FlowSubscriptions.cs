using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Marketgrade.Backend;

/// <summary>The live hub connections that watch the flow; the flow is only sampled and pushed while there is one.</summary>
public sealed class FlowSubscriptions
{
    public const string Group = "flow";

    private readonly ConcurrentDictionary<string, bool> _connections = new();
    private readonly Lock _lock = new();
    private TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Count => _connections.Count;

    public async Task AddAsync(string connectionId, IGroupManager groups)
    {
        await groups.AddToGroupAsync(connectionId, Group);
        _connections[connectionId] = true;
        Changed();
    }

    public async Task RemoveAsync(string connectionId, IGroupManager groups)
    {
        await groups.RemoveFromGroupAsync(connectionId, Group);
        Disconnected(connectionId);
    }

    public void Disconnected(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out _))
        {
            Changed();
        }
    }

    /// <summary>Completes once at least one connection is subscribed.</summary>
    public Task WaitForSubscriberAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _subscribed.Task.WaitAsync(cancellationToken);
        }
    }

    private void Changed()
    {
        lock (_lock)
        {
            if (!_connections.IsEmpty)
            {
                _subscribed.TrySetResult();
            }
            else if (_subscribed.Task.IsCompleted)
            {
                _subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
