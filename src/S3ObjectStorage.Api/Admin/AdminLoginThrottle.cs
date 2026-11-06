using System.Collections.Concurrent;

namespace S3ObjectStorage.Api.Admin;

/// <summary>
/// 管理端登录失败节流：同一来源 IP 在时间窗内连续失败达到阈值后暂时拒绝登录，
/// 用于抵御在线暴力破解。状态保存在进程内，多实例部署时应换成 Redis 等共享存储。
/// </summary>
public sealed class AdminLoginThrottle
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>判断某个来源是否已被锁定；<paramref name="retryAfter"/> 为剩余锁定时间。</summary>
    public bool IsBlocked(string source, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(source, out var entry))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (entry)
        {
            if (entry.LockedUntil > now)
            {
                retryAfter = entry.LockedUntil - now;
                return true;
            }

            // 窗口过期后重新计数。
            if (now - entry.WindowStart > Window)
            {
                entry.Failures = 0;
                entry.WindowStart = now;
            }

            return false;
        }
    }

    public void RecordFailure(string source)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _entries.GetOrAdd(source, static _ => new Entry { WindowStart = DateTimeOffset.UtcNow });

        lock (entry)
        {
            if (now - entry.WindowStart > Window)
            {
                entry.Failures = 0;
                entry.WindowStart = now;
            }

            entry.Failures++;
            if (entry.Failures >= MaxFailures)
            {
                entry.LockedUntil = now + Lockout;
                entry.Failures = 0;
            }
        }

        PruneIfNeeded(now);
    }

    public void Reset(string source) => _entries.TryRemove(source, out _);

    private void PruneIfNeeded(DateTimeOffset now)
    {
        if (_entries.Count < 10_000)
        {
            return;
        }

        foreach (var (key, entry) in _entries)
        {
            if (entry.LockedUntil < now && now - entry.WindowStart > Window)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset LockedUntil;
    }
}
