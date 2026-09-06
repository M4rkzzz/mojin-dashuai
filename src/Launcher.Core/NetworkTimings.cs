using System.Collections.Concurrent;
using System.Diagnostics;

namespace Boshan.Launcher;

public sealed record NetworkTiming(DateTimeOffset At, string Stage, long ElapsedMs, bool Success, string? Target, string? Instance);

/// <summary>Bounded, payload-free timings for the existing diagnostic export.</summary>
public static class NetworkTimings
{
    private static readonly ConcurrentQueue<NetworkTiming> records = new();
    public static NetworkTiming[] Snapshot() => records.ToArray();
    public static void Record(string stage, long elapsedMs, bool success, string? target = null, string? instance = null)
    {
        // Only pass a host/instance identifier, never a URL, player name or exception text.
        if (target is not null && (target.Length > 253 || target.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not ':' and not '[' and not ']'))) target = null;
        records.Enqueue(new(DateTimeOffset.UtcNow, stage, Math.Max(0, elapsedMs), success, target, instance));
        while (records.Count > 256) records.TryDequeue(out _);
    }
    public static async Task<T> Measure<T>(string stage, Func<Task<T>> action, string? target = null, string? instance = null)
    {
        var start = Stopwatch.GetTimestamp(); bool success = false;
        try { var result = await action().ConfigureAwait(false); success = true; return result; }
        finally { Record(stage, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds, success, target, instance); }
    }
}
