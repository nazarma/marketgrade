namespace Marketgrade.Engine.Flow;

public enum FlowKind : byte
{
    /// <summary>The amount at one price level changed.</summary>
    BookChange,

    Trade,
}

public enum FlowSide : byte
{
    /// <summary>A bid level, or a trade whose taker bought.</summary>
    Buy,

    /// <summary>An ask level, or a trade whose taker sold.</summary>
    Sell,
}

/// <summary>One book change or trade for the live flow view. Prices and amounts are scaled by the pair's decimals.</summary>
/// <param name="TimeNs">Unix nanoseconds: arrival time for a book change, the exchange's trade time for a trade.</param>
/// <param name="Amount">For a trade its amount; for a book change the signed change in amount at the level.</param>
public readonly record struct FlowEvent(long TimeNs, FlowKind Kind, FlowSide Side, long Price, long Amount);
