using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Marketgrade.Backend;

/// <summary>One answer of a time server, tied to the monotonic clock.</summary>
/// <param name="RoundTrip">Time on the network both ways, without the time the server took; the error of the answer is at most half of it.</param>
/// <param name="Timestamp"><see cref="Stopwatch"/> timestamp when the answer arrived.</param>
/// <param name="NtpUnixNs">The server's time at <paramref name="Timestamp"/>, Unix nanoseconds.</param>
/// <param name="WallUnixNs">This computer's clock at <paramref name="Timestamp"/>, Unix nanoseconds.</param>
public readonly record struct SntpSample(TimeSpan RoundTrip, long Timestamp, long NtpUnixNs, long WallUnixNs)
{
    /// <summary>How far this clock is ahead of the server's: positive means local time is later.</summary>
    public TimeSpan Offset => TimeSpan.FromTicks((WallUnixNs - NtpUnixNs) / 100);
}

/// <summary>A minimal SNTP v4 client (RFC 4330): one request, one answer.</summary>
public static class SntpClient
{
    public const int Port = 123;

    private const int PacketSize = 48;

    private static readonly DateTime s_ntpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Asks <paramref name="host"/> for the time. Throws <see cref="SocketException"/>, <see cref="TimeoutException"/>
    /// or <see cref="InvalidDataException"/>.
    /// </summary>
    public static async Task<SntpSample> QueryAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        using var udp = new UdpClient(address.AddressFamily);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var request = new byte[PacketSize];
        // Leap indicator 0, version 4, mode 3 (client).
        request[0] = 0x23;
        var sent = DateTime.UtcNow;
        var sentTimestamp = Stopwatch.GetTimestamp();
        WriteTimestamp(request.AsSpan(40), sent);
        try
        {
            await udp.SendAsync(request, new IPEndPoint(address, port), deadline.Token);
            while (true)
            {
                var result = await udp.ReceiveAsync(deadline.Token);
                var receivedTimestamp = Stopwatch.GetTimestamp();
                var wall = DateTime.UtcNow;
                var answer = result.Buffer;
                // Only the answer to this request: it echoes our transmit time as its originate time.
                if (answer.Length < PacketSize || !answer.AsSpan(24, 8).SequenceEqual(request.AsSpan(40, 8)))
                {
                    continue;
                }
                if ((answer[0] & 0x07) != 4 || answer[1] == 0)
                {
                    throw new InvalidDataException($"{host} sent no time (mode {answer[0] & 0x07}, stratum {answer[1]})");
                }
                var serverReceived = ReadTimestamp(answer.AsSpan(32));
                var serverSent = ReadTimestamp(answer.AsSpan(40));
                // The local times on the monotonic clock, so a step of the wall clock during the request does not count.
                var elapsed = Stopwatch.GetElapsedTime(sentTimestamp, receivedTimestamp);
                var received = sent + elapsed;
                // Server minus local, averaged over the way there and back.
                var serverAhead = ((serverReceived - sent) + (serverSent - received)) / 2;
                return new SntpSample(
                    elapsed - (serverSent - serverReceived),
                    receivedTimestamp,
                    UnixNs(received + serverAhead),
                    UnixNs(wall));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{host} did not answer within {timeout.TotalSeconds:0.#} s");
        }
    }

    /// <summary>Writes an NTP timestamp: seconds since 1900 and a 32-bit fraction, big-endian.</summary>
    public static void WriteTimestamp(Span<byte> destination, DateTime time)
    {
        var ticks = (time - s_ntpEpoch).Ticks;
        var seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        var fraction = (ulong)(ticks % TimeSpan.TicksPerSecond) * 0x1_0000_0000UL / TimeSpan.TicksPerSecond;
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)fraction);
    }

    public static DateTime ReadTimestamp(ReadOnlySpan<byte> source)
    {
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(source);
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        return s_ntpEpoch.AddTicks(seconds * TimeSpan.TicksPerSecond + (long)(fraction * (ulong)TimeSpan.TicksPerSecond >> 32));
    }

    private static long UnixNs(DateTime time) => (time.Ticks - DateTime.UnixEpoch.Ticks) * 100;
}
