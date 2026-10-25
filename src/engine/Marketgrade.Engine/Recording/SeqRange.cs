namespace Marketgrade.Engine.Recording;

/// <summary>Messages not recorded: the first and last sequence number and how many.</summary>
internal struct SeqRange
{
    public long First;
    public long Last;
    public long Count;

    public readonly bool IsEmpty => Count == 0;

    public void Add(long first, long last, long count)
    {
        if (count == 0)
        {
            return;
        }
        First = Count == 0 ? first : Math.Min(First, first);
        Last = Count == 0 ? last : Math.Max(Last, last);
        Count += count;
    }

    public void Add(long seq) => Add(seq, seq, 1);

    public void Add(SeqRange other) => Add(other.First, other.Last, other.Count);

    public void Clear() => this = default;
}
