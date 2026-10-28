namespace Marketgrade.Engine.Tests;

public class ConnectionEventLogTests
{
    [Fact]
    public void New_events_are_queued_for_pushing_and_the_queue_keeps_the_newest()
    {
        var log = new ConnectionEventLog(3);
        for (var i = 1; i <= 5; i++)
        {
            log.Add("Kraken", $"Event {i}");
        }

        var pushed = new List<string>();
        while (log.Added.TryRead(out var added))
        {
            pushed.Add(added.Event);
        }

        Assert.Equal(["Event 3", "Event 4", "Event 5"], pushed);
        Assert.Equal(["Event 3", "Event 4", "Event 5"], log.GetRecent(10).Select(e => e.Event));
    }
}
