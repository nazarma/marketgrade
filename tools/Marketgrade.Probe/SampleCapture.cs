using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Marketgrade.Engine;

namespace Marketgrade.Probe;

/// <summary>
/// Saves real messages from each exchange as test fixtures: the first few of every kind, plus the
/// answers to a valid and an invalid request so error messages are captured too.
/// </summary>
internal static class SampleCapture
{
    private const int PerKind = 3;
    private const int TrimAbove = 1024 * 1024;
    private const int KeepArrayItems = 200;

    private static readonly Dictionary<string, string[]> s_extraRequests = new()
    {
        ["Kraken"] =
        [
            """{"method":"ping","req_id":1}""",
            """{"method":"subscribe","params":{"channel":"book","symbol":["NOPE/USD"]}}""",
        ],
        ["Coinbase"] =
        [
            """{"type":"subscribe","channel":"nope"}""",
            """{"type":"nope"}""",
        ],
        ["Binance"] =
        [
            """{"method":"LIST_SUBSCRIPTIONS","id":1}""",
            """{"method":"SUBSCRIBE","params":"nope","id":2}""",
            """{"method":"NOPE","id":3}""",
        ],
    };

    public static async Task RunAsync(IReadOnlyList<ExchangeAdapter> adapters, string directory, TimeSpan duration)
    {
        Directory.CreateDirectory(directory);
        await Task.WhenAll(adapters.Select(adapter => CaptureAsync(adapter, directory, duration)));
    }

    private static async Task CaptureAsync(ExchangeAdapter adapter, string directory, TimeSpan duration)
    {
        var name = adapter.Name.ToLowerInvariant();
        var counts = new Dictionary<MessageKind, int>();
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(adapter.Uri, CancellationToken.None);
        }
        catch (WebSocketException e)
        {
            Console.WriteLine($"{adapter.Name}: connect failed (HTTP {(int)socket.HttpStatusCode}): {e.Message}");
            return;
        }
        foreach (var message in adapter.SubscribeMessages)
        {
            await socket.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        // The extra requests go out near the end, so a server that disconnects on errors does not cut the capture short.
        using var stop = new CancellationTokenSource(duration);
        _ = Task.Run(async () =>
        {
            await Task.Delay(duration * 0.7);
            foreach (var request in s_extraRequests.GetValueOrDefault(adapter.Name, []))
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });

        var buffer = new byte[64 * 1024];
        var total = 0;
        try
        {
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), stop.Token);
                    length += result.Count;
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"{adapter.Name}: closed by server ({socket.CloseStatus} {socket.CloseStatusDescription})");
                    break;
                }

                total++;
                var message = buffer.AsSpan(0, length).ToArray();
                var kind = adapter.Classify(message);
                var count = counts.GetValueOrDefault(kind);
                if (count < PerKind)
                {
                    counts[kind] = count + 1;
                    var suffix = message.Length > TrimAbove ? "-trimmed" : "";
                    var path = Path.Combine(directory, $"{name}-{kind.ToString().ToLowerInvariant()}-{count + 1}{suffix}.json");
                    await File.WriteAllBytesAsync(path, message.Length > TrimAbove ? Trim(message) : message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine($"{adapter.Name}: {total} messages, saved {string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}"))}");
    }

    /// <summary>Shortens every long JSON array, so a multi-megabyte snapshot fits in the repository.</summary>
    private static byte[] Trim(byte[] message)
    {
        var root = JsonNode.Parse(message)!;
        TrimArrays(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static void TrimArrays(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                while (array.Count > KeepArrayItems)
                {
                    array.RemoveAt(array.Count - 1);
                }
                foreach (var item in array)
                {
                    TrimArrays(item);
                }
                break;
            case JsonObject obj:
                foreach (var property in obj)
                {
                    TrimArrays(property.Value);
                }
                break;
        }
    }
}
