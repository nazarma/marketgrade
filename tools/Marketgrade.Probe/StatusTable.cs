using Marketgrade.Engine;

namespace Marketgrade.Probe;

internal static class StatusTable
{
    public static void Print(EngineStatus status)
    {
        Console.WriteLine($"  {"Exchange",-9} {"State",-12} {"data/s",8} {"KB/s",8} {"data",9} {"heartb.",8} {"control",8} {"errors",7} {"reconn.",7}  last error");
        foreach (var e in status.Exchanges)
        {
            Console.WriteLine(
                $"  {e.Name,-9} {e.State,-12} {e.MessagesPerSecond,8:0.0} {e.BytesPerSecond / 1024,8:0.0} {e.DataMessages,9} " +
                $"{e.HeartbeatMessages,8} {e.ControlMessages,8} {e.ErrorMessages,7} {e.ReconnectCount,7}  {e.LastError}");
        }
        foreach (var e in status.Exchanges)
        {
            Console.WriteLine(
                $"  {e.Name,-9} book {e.BookState,-11} bid {e.BestBid ?? "-",10} ask {e.BestAsk ?? "-",10} " +
                $"delay {Ms(e.DelayP50Ms)}/{Ms(e.DelayP99Ms)} ms (catch-up {e.CatchUpMessages}, max {Ms(e.CatchUpMaxDelayMs)} ms)  " +
                $"{(e.ExchangeHoldP50Ms is null ? "" : $"hold {Ms(e.ExchangeHoldP50Ms)}/{Ms(e.ExchangeHoldP99Ms)} ms  ")}queue {e.QueueWaitP50Ns?.ToString() ?? "-"}/{e.QueueWaitP99Ns?.ToString() ?? "-"} ns  " +
                $"processing {e.ProcessingP50Ns?.ToString() ?? "-"}/{e.ProcessingP99Ns?.ToString() ?? "-"} ns  " +
                $"levels {e.BidLevels}/{e.AskLevels}  gaps {e.GapsToday}{(e.LastGap is { } gap ? $" (last: {gap.Cause})" : "")}");
        }
        Console.WriteLine($"  Clock offset: {(status.ClockOffsetMs is { } offset ? $"{offset:0.0} ms" : "unknown, delays uncorrected")}");
    }

    public static void PrintSummary(EngineStatus status, int seconds)
    {
        foreach (var e in status.Exchanges)
        {
            var reachable = e.ConnectionId > 0 ? "reachable" : e.State == ConnectionState.Blocked ? "BLOCKED in this region" : "NOT reachable";
            Console.WriteLine($"  {e.Name} {e.Pair}: {reachable}, state {e.State}");
            Console.WriteLine(
                $"    {e.DataMessages} data messages ({(double)e.DataMessages / seconds:0.0}/s), {e.HeartbeatMessages} heartbeats, " +
                $"{e.ControlMessages} control, {e.ErrorMessages} errors, {e.Bytes / 1024.0 / 1024.0:0.00} MB");
            Console.WriteLine($"    {e.ConnectionId} connection(s), {e.ReconnectCount} reconnect(s){(e.LastError is null ? "" : $", last error: {e.LastError}")}");
            Console.WriteLine($"    book {e.BookState}, most levels seen {e.MaxBidLevels} bids / {e.MaxAskLevels} asks, {e.GapsToday} gap(s) today");
        }
    }

    private static string Ms(double? value) => value is { } ms ? ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "-";
}
