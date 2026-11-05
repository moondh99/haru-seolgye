using System.Collections.Concurrent;
using Haru.Api.Config;

namespace Haru.Api.Infrastructure;

/// <summary>Per-client sliding window, global concurrency gate and a daily cap. All in memory; no personal data is stored.</summary>
public sealed class UsageLimiter(HaruOptions options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows = new();
    private readonly SemaphoreSlim _gate = new(options.MaxConcurrent, options.MaxConcurrent);
    private readonly object _dailyLock = new();
    private string _dailyDay = "";
    private int _dailyCount;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    public enum Verdict { Ok, RateLimited, DailyCap, Busy }

    public sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    public (Verdict Verdict, Lease? Lease) TryEnter(string clientKey)
    {
        var now = Now();
        var window = TimeSpan.FromMinutes(options.WindowMinutes);

        var q = _windows.GetOrAdd(clientKey, _ => new Queue<DateTimeOffset>());

        // Check and count under the same locks, so parallel requests from one client cannot pass the limit together.
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
            if (q.Count >= options.PlansPerWindow) return (Verdict.RateLimited, null);

            lock (_dailyLock)
            {
                var day = now.ToOffset(Clock.KstOffset).ToString("yyyy-MM-dd");
                if (day != _dailyDay) { _dailyDay = day; _dailyCount = 0; }
                if (_dailyCount >= options.DailyCap) return (Verdict.DailyCap, null);

                if (!_gate.Wait(0)) return (Verdict.Busy, null);

                q.Enqueue(now);
                _dailyCount++;
            }
        }

        TrimIfLarge(now, window);
        return (Verdict.Ok, new Lease(_gate));
    }

    private void TrimIfLarge(DateTimeOffset now, TimeSpan window)
    {
        if (_windows.Count < 2000) return;
        foreach (var (key, q) in _windows)
        {
            lock (q)
            {
                while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
                if (q.Count == 0) _windows.TryRemove(key, out _);
            }
        }
    }

    public int Window => options.WindowMinutes;
}
