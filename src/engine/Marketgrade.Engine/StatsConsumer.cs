using System.Diagnostics;
using System.Threading.Channels;

namespace Marketgrade.Engine;

/// <summary>Classifies every message of one exchange and counts messages, bytes and kinds.</summary>
public sealed class StatsConsumer(ExchangeAdapter adapter)
{
    private long _dataMessages;
    private long _heartbeatMessages;
    private long _controlMessages;
    private long _errorMessages;
    private long _bytes;

    public long DataMessages => Interlocked.Read(ref _dataMessages);

    public long HeartbeatMessages => Interlocked.Read(ref _heartbeatMessages);

    public long ControlMessages => Interlocked.Read(ref _controlMessages);

    public long ErrorMessages => Interlocked.Read(ref _errorMessages);

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Runs until the queue is completed and empty, so every buffer is released.</summary>
    public async Task RunAsync(ChannelReader<MessageBuffer> reader)
    {
        // No cancellation token: the channel then reuses its waiter and the loop does not allocate.
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var buffer))
            {
                Process(buffer);
            }
        }
    }

    internal void Process(MessageBuffer buffer)
    {
        Debug.Assert(buffer.Length > 0, "Buffer was released before this consumer read it");
        switch (adapter.Classify(buffer.Span))
        {
            case MessageKind.Data:
                Interlocked.Increment(ref _dataMessages);
                break;
            case MessageKind.Heartbeat:
                Interlocked.Increment(ref _heartbeatMessages);
                break;
            case MessageKind.Control:
                Interlocked.Increment(ref _controlMessages);
                break;
            default:
                Interlocked.Increment(ref _errorMessages);
                break;
        }
        Interlocked.Add(ref _bytes, buffer.Length);
        buffer.Release();
    }
}
