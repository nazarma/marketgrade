using System.Threading.Channels;

namespace Marketgrade.Engine;

/// <summary>
/// Writes to a channel and wakes its reader thread through <paramref name="signal"/> after each write and on
/// completion. <see cref="ManualResetEventSlim.Set"/> on an event that is already set costs almost nothing.
/// </summary>
internal sealed class SignalingWriter(ChannelWriter<MessageBuffer> inner, ManualResetEventSlim signal) : ChannelWriter<MessageBuffer>
{
    public override bool TryWrite(MessageBuffer item)
    {
        if (!inner.TryWrite(item))
        {
            return false;
        }
        signal.Set();
        return true;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
        inner.WaitToWriteAsync(cancellationToken);

    public override bool TryComplete(Exception? error = null)
    {
        var completed = inner.TryComplete(error);
        signal.Set();
        return completed;
    }
}
