using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.Flow;

/// <summary>
/// One exchange's live flow for a reader on another thread: its <see cref="FlowRing"/>, running counts of
/// everything received, and the best levels of its book.
/// </summary>
public sealed class ExchangeFlow : IExchangeFlow
{
    private readonly BookWorker _worker;

    internal ExchangeFlow(BookWorker worker) => _worker = worker;

    public int ExchangeId => _worker.Adapter.Id;

    public string Exchange => _worker.Adapter.Name;

    public string Pair => _worker.Adapter.Pair;

    /// <summary>Null while the exchange keeps no book; prices and amounts in the ring use these decimals.</summary>
    public PairRules? Rules => _worker.Rules;

    public FlowRing Ring => _worker.Flow;

    /// <summary>Levels changed by updates to a valid book, at any distance from the mid price; counted whether flow is enabled or not.</summary>
    public long BookChanges => _worker.BookChanges;

    public long Trades => _worker.Trades;

    /// <summary>The latest published copy of the best levels; see <see cref="MarketEngine.GetBook"/>.</summary>
    public BookSnapshot? ReadBook() => _worker.ReadSnapshot();
}
