using System.Collections.Concurrent;

namespace FengSync.Core.SftpServer;

/// <summary>Small deterministic policy component mirrored by the isolated protocol host.</summary>
public sealed class SftpAuthenticationRateLimiter
{
    private readonly int _maxFailures;
    private readonly TimeSpan _blockDuration;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public SftpAuthenticationRateLimiter(int maxFailures, TimeSpan blockDuration, Func<DateTimeOffset>? clock = null)
    {
        if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
        if (blockDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(blockDuration));
        _maxFailures = maxFailures; _blockDuration = blockDuration; _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool TryAllow(string address, string userName)
    {
        var key = Key(address, userName); var now = _clock();
        if (!_attempts.TryGetValue(key, out var state)) return true;
        if (state.BlockedUntil is { } until && until > now) return false;
        if (state.BlockedUntil is not null) _attempts.TryRemove(key, out _);
        return true;
    }

    public void RecordFailure(string address, string userName)
    {
        var key = Key(address, userName); var now = _clock();
        _attempts.AddOrUpdate(key, _ => new AttemptState(1, null), (_, previous) =>
        {
            var failures = previous.BlockedUntil is { } until && until <= now ? 1 : previous.Failures + 1;
            return new(failures, failures >= _maxFailures ? now.Add(_blockDuration) : null);
        });
    }

    public void RecordSuccess(string address, string userName) => _attempts.TryRemove(Key(address, userName), out _);
    private static string Key(string address, string userName) => address.Trim() + "\n" + userName.Trim();
    private sealed record AttemptState(int Failures, DateTimeOffset? BlockedUntil);
}
