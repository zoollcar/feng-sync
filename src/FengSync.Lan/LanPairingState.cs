namespace FengSync.Lan;

/// <summary>What the armed code said about an attempt.</summary>
public enum LanPairVerdict
{
    /// <summary>The code matched and is now spent.</summary>
    Accepted,

    /// <summary>Well formed, not the armed code.</summary>
    BadCode,

    /// <summary>The armed code aged out; a fresh one is available.</summary>
    CodeExpired,

    /// <summary>This source has spent its wrong-code budget inside the attempt window.</summary>
    RateLimited,
}

/// <summary>
/// The short-lived code a peer must present to become a device this application trusts.
/// </summary>
/// <remarks>
/// <para>
/// Single use is what makes the code safe to print inside a QR that gets photographed and left on a screen:
/// after one match it is worth nothing. A <em>wrong</em> attempt deliberately does not spend it — a six
/// character code typed by hand on a phone will be mis-typed, and burning the pairing over that would make
/// the user regenerate every attempt. Guessing is bounded by the per-source budget instead, so the two
/// properties hold at once: a typo costs nothing and a brute force costs the source its window.
/// </para>
/// <para>
/// <see cref="Arm"/> replaces an expired or spent code on its own, because a window left open for ten
/// minutes should pair a phone without the user hunting for a button; the countdown in the UI is the same
/// number this type reads off the injected clock.
/// </para>
/// </remarks>
public sealed class LanPairingState(Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? DefaultClock;
    private static DateTimeOffset DefaultClock() => DateTimeOffset.UtcNow;
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.OrdinalIgnoreCase);

    private string _code = "";
    private DateTimeOffset _armedUtc = DateTimeOffset.MinValue;
    private bool _spent;

    /// <summary>The code currently displayed. Reading it does not arm anything; call <see cref="Arm"/>.</summary>
    public string Code
    {
        get
        {
            lock (_gate) return _code;
        }
    }

    /// <summary>Seconds left on the armed code. Zero once it has aged out or been spent.</summary>
    public int RemainingSeconds
    {
        get
        {
            lock (_gate) return Remaining(_clock());
        }
    }

    public bool IsAccepting
    {
        get
        {
            lock (_gate) return !_spent && Remaining(_clock()) > 0;
        }
    }

    /// <summary>Returns the live code, replacing it first if the previous one was spent or has aged out.</summary>
    public string Arm()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_code.Length == 0 || _spent || Remaining(now) <= 0)
            {
                _code = LanPairCode.Generate();
                _armedUtc = now;
                _spent = false;
            }

            return _code;
        }
    }

    /// <summary>Drops the code outright, used when the service stops so nothing stays answerable.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _code = "";
            _spent = true;
            _armedUtc = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Judges one presented code. Only a match spends it; a mismatch counts against
    /// <see cref="LanProtocol.MaximumFailedAttempts"/> for that source and leaves the code armed.
    /// </summary>
    /// <param name="source">
    /// The caller's address, which is the thing being throttled. The code itself is not per-source.
    /// </param>
    public LanPairVerdict TryConsume(string? rawCode, string source)
    {
        lock (_gate)
        {
            var now = _clock();
            if (!LanPairCode.TryNormalise(rawCode, out var presented)) return _spent || Remaining(now) <= 0
                    ? LanPairVerdict.CodeExpired
                    : LanPairVerdict.BadCode;

            if (_spent || Remaining(now) <= 0) return LanPairVerdict.CodeExpired;
            if (IsThrottled(source, now)) return LanPairVerdict.RateLimited;

            if (!string.Equals(presented, _code, StringComparison.Ordinal))
            {
                Record(source, now);
                return LanPairVerdict.BadCode;
            }

            _spent = true;
            _failures.Clear();
            return LanPairVerdict.Accepted;
        }
    }

    private int Remaining(DateTimeOffset now)
        => _code.Length == 0 ? 0 : Math.Max(0, LanProtocol.PairCodeLifetimeSeconds - (int)(now - _armedUtc).TotalSeconds);

    private bool IsThrottled(string source, DateTimeOffset now)
        => _failures.TryGetValue(source, out var attempts) && Pruned(attempts, now).Count >= LanProtocol.MaximumFailedAttempts;

    private void Record(string source, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(source, out var attempts)) _failures[source] = attempts = [];
        Pruned(attempts, now).Add(now);
    }

    /// <summary>
    /// Trims the window in place and hands the same list back, so a source that has been quiet for a minute
    /// is judged on the failures still inside the window rather than on its lifetime history.
    /// </summary>
    private static List<DateTimeOffset> Pruned(List<DateTimeOffset> attempts, DateTimeOffset now)
    {
        attempts.RemoveAll(at => (now - at).TotalSeconds > LanProtocol.AttemptWindowSeconds);
        return attempts;
    }
}
