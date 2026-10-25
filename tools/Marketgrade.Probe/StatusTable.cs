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
        }
    }
}
