using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.Tests.Books;

public class OrderBookSideTests
{
    [Fact]
    public void Set_returns_the_amount_the_level_had_before()
    {
        var bids = new OrderBookSide(bids: true, capacity: 8);

        Assert.Equal(0, bids.Set(100, 5));
        Assert.Equal(5, bids.Set(100, 7));
        Assert.Equal(7, bids.Set(100, 0));
        Assert.Equal(0, bids.Set(100, 0));
    }

    [Fact]
    public void Asks_are_kept_lowest_first()
    {
        var asks = new OrderBookSide(bids: false, capacity: 8);

        asks.Set(102, 1);
        asks.Set(100, 2);
        asks.Set(101, 3);

        Assert.Equal([(100L, 2L), (101L, 3L), (102L, 1L)], Levels(asks));
    }

    [Fact]
    public void Bids_are_kept_highest_first()
    {
        var bids = new OrderBookSide(bids: true, capacity: 8);

        bids.Set(99, 1);
        bids.Set(101, 2);
        bids.Set(100, 3);

        Assert.Equal([(101L, 2L), (100L, 3L), (99L, 1L)], Levels(bids));
    }

    [Fact]
    public void Existing_level_is_updated_in_place()
    {
        var asks = new OrderBookSide(bids: false, capacity: 8);
        asks.Set(100, 1);
        asks.Set(101, 1);

        asks.Set(100, 7);

        Assert.Equal([(100L, 7L), (101L, 1L)], Levels(asks));
    }

    [Fact]
    public void Zero_amount_deletes_the_level_and_unknown_levels_are_ignored()
    {
        var bids = new OrderBookSide(bids: true, capacity: 8);
        bids.Set(100, 1);
        bids.Set(99, 2);
        bids.Set(98, 3);

        bids.Set(99, 0);
        bids.Set(50, 0);

        Assert.Equal([(100L, 1L), (98L, 3L)], Levels(bids));
    }

    [Fact]
    public void Truncate_keeps_the_best_levels()
    {
        var asks = new OrderBookSide(bids: false, capacity: 8);
        for (var price = 105; price >= 100; price--)
        {
            asks.Set(price, 1);
        }

        asks.Truncate(3);
        asks.Truncate(10);

        Assert.Equal([100L, 101L, 102L], Levels(asks).Select(l => l.Price));
    }

    [Fact]
    public void Full_side_doubles_and_keeps_its_levels()
    {
        var bids = new OrderBookSide(bids: true, capacity: 2);

        bids.Set(1, 1);
        bids.Set(3, 1);
        bids.Set(2, 1);
        Assert.Equal(4, bids.Capacity);
        bids.Set(5, 1);
        bids.Set(4, 1);

        Assert.Equal(8, bids.Capacity);
        Assert.Equal([5L, 4L, 3L, 2L, 1L], Levels(bids).Select(l => l.Price));
    }

    [Fact]
    public void Max_count_remembers_the_largest_size()
    {
        var asks = new OrderBookSide(bids: false, capacity: 8);
        asks.Set(1, 1);
        asks.Set(2, 1);
        asks.Set(3, 1);
        asks.Set(2, 0);
        asks.Clear();
        asks.Set(9, 1);

        Assert.Equal(1, asks.Count);
        Assert.Equal(3, asks.MaxCount);
    }

    [Fact]
    public void Random_changes_match_a_sorted_dictionary()
    {
        var random = new Random(42);
        var bids = new OrderBookSide(bids: true, capacity: 16);
        var expected = new SortedDictionary<long, long>(Comparer<long>.Create((a, b) => b.CompareTo(a)));
        for (var i = 0; i < 20_000; i++)
        {
            var price = random.Next(1, 500);
            var amount = random.Next(3) == 0 ? 0 : random.Next(1, 1_000);
            bids.Set(price, amount);
            if (amount == 0)
            {
                expected.Remove(price);
            }
            else
            {
                expected[price] = amount;
            }
        }

        Assert.Equal(expected.Select(p => (p.Key, p.Value)), Levels(bids));
    }

    private static List<(long Price, long Amount)> Levels(OrderBookSide side) =>
        [.. Enumerable.Range(0, side.Count).Select(i => (side.PriceAt(i), side.AmountAt(i)))];
}
