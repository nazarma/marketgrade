using System.Threading.Channels;

namespace Marketgrade.Engine.History;

/// <summary>
/// What the engine publishes for persistence: minute statistics, completed gaps and reconnects. The engine's
/// threads only ever call <c>TryWrite</c>; each queue keeps the newest <see cref="Capacity"/> records, so a
/// missing or slow reader costs memory up to that limit and never slows the engine.
/// </summary>
public sealed class HistoryFeed
{
    /// <summary>About two days of minutes for three exchanges.</summary>
    public const int Capacity = 10_000;

    private readonly Channel<MinuteStats> _minutes = Create<MinuteStats>();
    private readonly Channel<GapRecord> _gaps = Create<GapRecord>();
    private readonly Channel<ReconnectRecord> _reconnects = Create<ReconnectRecord>();

    public ChannelReader<MinuteStats> Minutes => _minutes.Reader;

    public ChannelReader<GapRecord> Gaps => _gaps.Reader;

    public ChannelReader<ReconnectRecord> Reconnects => _reconnects.Reader;

    internal void Add(MinuteStats minute) => _minutes.Writer.TryWrite(minute);

    internal void Add(GapRecord gap) => _gaps.Writer.TryWrite(gap);

    internal void Add(ReconnectRecord reconnect) => _reconnects.Writer.TryWrite(reconnect);

    private static Channel<T> Create<T>() => Channel.CreateBounded<T>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
}
