using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.Flow;

/// <summary>One exchange's live flow as a reader on another thread sees it; <see cref="ExchangeFlow"/> is the engine's.</summary>
public interface IExchangeFlow
{
    int ExchangeId { get; }

    string Exchange { get; }

    string Pair { get; }

    /// <summary>Null while the exchange keeps no book; prices and amounts in the ring use these decimals.</summary>
    PairRules? Rules { get; }

    FlowRing Ring { get; }

    /// <summary>Levels changed by updates to a valid book, at any distance from the mid price.</summary>
    long BookChanges { get; }

    long Trades { get; }

    /// <summary>The latest published copy of the best levels, or null before the first one.</summary>
    BookSnapshot? ReadBook();
}
