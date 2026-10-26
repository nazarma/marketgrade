using System.Net;
using System.Net.Sockets;

namespace Marketgrade.Backend.Tests;

public class SntpClientTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Offset_is_how_far_the_local_clock_is_ahead()
    {
        // The server's clock is 2 seconds behind ours, so ours is 2 seconds ahead.
        using var server = new FakeTimeServer(request => Answer(request, DateTime.UtcNow.AddSeconds(-2)));

        var offset = await SntpClient.QueryOffsetAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(2), Token);

        Assert.InRange(offset.TotalMilliseconds, 1_950, 2_050);
    }

    [Fact]
    public async Task Answer_without_time_is_rejected()
    {
        using var server = new FakeTimeServer(request =>
        {
            var answer = Answer(request, DateTime.UtcNow);
            answer[1] = 0;
            return answer;
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SntpClient.QueryOffsetAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(2), Token));
    }

    [Fact]
    public async Task Silent_server_times_out()
    {
        using var server = new FakeTimeServer(_ => null);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            SntpClient.QueryOffsetAsync("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(200), Token));
    }

    [Fact]
    public void Timestamps_round_trip_to_well_under_a_microsecond()
    {
        var time = new DateTime(2026, 10, 26, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1234);
        var bytes = new byte[8];

        SntpClient.WriteTimestamp(bytes, time);

        Assert.InRange((SntpClient.ReadTimestamp(bytes) - time).Duration().Ticks, 0, 1);
    }

    /// <summary>A server answer: mode 4, stratum 2, our transmit time echoed, both server times <paramref name="now"/>.</summary>
    private static byte[] Answer(byte[] request, DateTime now)
    {
        var answer = new byte[48];
        answer[0] = 0x24;
        answer[1] = 2;
        request.AsSpan(40, 8).CopyTo(answer.AsSpan(24));
        SntpClient.WriteTimestamp(answer.AsSpan(32), now);
        SntpClient.WriteTimestamp(answer.AsSpan(40), now);
        return answer;
    }

    private sealed class FakeTimeServer : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();

        public FakeTimeServer(Func<byte[], byte[]?> answer)
        {
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var request = await _udp.ReceiveAsync(_stop.Token);
                        if (answer(request.Buffer) is { } response)
                        {
                            await _udp.SendAsync(response, request.RemoteEndPoint, _stop.Token);
                        }
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
        }
    }
}
