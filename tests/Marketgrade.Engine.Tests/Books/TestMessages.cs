using System.Text;

namespace Marketgrade.Engine.Tests.Books;

/// <summary>Real message sequences from <c>Fixtures/sequences</c> (one message per line) as pooled buffers.</summary>
internal static class TestMessages
{
    public static string[] Sequence(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sequences", name));

    public static MessageBuffer Buffer(MessageBufferPool pool, string text, long seq = 1, long connectionId = 1, long arrivalUnixNs = 0) =>
        Buffer(pool, Encoding.UTF8.GetBytes(text), seq, connectionId, arrivalUnixNs);

    public static MessageBuffer Buffer(MessageBufferPool pool, byte[] bytes, long seq = 1, long connectionId = 1, long arrivalUnixNs = 0)
    {
        var buffer = pool.Rent();
        while (buffer.Bytes.Length < bytes.Length)
        {
            buffer.Grow(0);
        }
        bytes.CopyTo(buffer.Bytes, 0);
        buffer.Length = bytes.Length;
        buffer.Seq = seq;
        buffer.ConnectionId = connectionId;
        buffer.ArrivalUnixNs = arrivalUnixNs;
        buffer.Counter = 1;
        return buffer;
    }
}
