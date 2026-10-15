using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers the code a person reads off one screen and types into another, and the state that decides whether
/// it still answers. Both halves are tested against an injected clock because the interesting behaviour —
/// a code that has aged out, a source that has spent its guesses — takes two minutes of real time to reach.
/// </summary>
public sealed class LanPairingStateTests
{
    [Fact]
    public void A_generated_code_is_exactly_one_code_and_reads_back_as_itself()
    {
        var code = LanPairCode.Generate();
        Assert.Equal(LanPairCode.Length, code.Length);
        Assert.All(code, character => Assert.True(LanPairCode.Alphabet.Contains(character), $"unexpected character {character}"));
        Assert.True(LanPairCode.TryNormalise(code, out var normalised));
        Assert.Equal(code, normalised);
    }

    [Fact]
    public void The_alphabet_holds_no_character_a_person_could_confuse_with_another()
    {
        // 0/O and 1/I are the two pairs that make a hand-typed code unreliable; the alphabet drops them, and
        // the state must not be able to drift back to a full base32 by accident.
        foreach (var character in "01OIoi")
            Assert.DoesNotContain(character, LanPairCode.Alphabet);
    }

    [Theory]
    [InlineData("abcdex", true, "ABCDEX")]
    [InlineData("abc-dex", true, "ABCDEX")]
    [InlineData("abc dex", true, "ABCDEX")]
    [InlineData("AbC.dEx", true, "ABCDEX")]
    [InlineData("  abcdeX  ", true, "ABCDEX")]
    [InlineData("ABCDE", false, "")]
    [InlineData("ABCDEFG", false, "")]
    [InlineData("abcdef0", false, "")]
    [InlineData("abcdef1", false, "")]
    [InlineData("abcde-", false, "")]
    [InlineData("", false, "")]
    [InlineData("      ", false, "")]
    public void Input_is_normalised_only_when_it_is_exactly_one_code(string input, bool expected, string normalised)
    {
        Assert.Equal(expected, LanPairCode.TryNormalise(input, out var actual));
        Assert.Equal(normalised, actual);
    }

    [Fact]
    public void Arming_twice_while_a_code_is_live_hands_back_the_same_code()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LanPairingState(() => now);
        var first = state.Arm();
        Assert.Equal(first, state.Arm());
        Assert.Equal(first, state.Code);
        Assert.True(state.IsAccepting);
    }

    [Fact]
    public void The_matching_code_is_spent_and_the_next_arm_is_a_different_code()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LanPairingState(() => now);
        var code = state.Arm();
        Assert.Equal(LanPairVerdict.Accepted, state.TryConsume(code, "192.168.1.9"));
        // A second presentation of the same code is the replay a single-use code exists to stop.
        Assert.Equal(LanPairVerdict.CodeExpired, state.TryConsume(code, "192.168.1.9"));
        Assert.NotEqual(code, state.Arm());
    }

    [Fact]
    public void A_wrong_code_does_not_spend_the_pairing_the_user_is_still_typing_into()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LanPairingState(() => now);
        var code = state.Arm();
        Assert.Equal(LanPairVerdict.BadCode, state.TryConsume("ZZZZZZ", "192.168.1.9"));
        Assert.True(state.IsAccepting);
        Assert.Equal(code, state.Code);
        Assert.Equal(LanPairVerdict.Accepted, state.TryConsume(code, "192.168.1.9"));
    }

    [Fact]
    public void A_code_ages_out_at_its_lifetime_and_arming_again_replaces_it()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LanPairingState(() => now);
        var code = state.Arm();
        Assert.Equal(LanProtocol.PairCodeLifetimeSeconds, state.RemainingSeconds);

        now = now.AddSeconds(LanProtocol.PairCodeLifetimeSeconds - 1);
        Assert.Equal(1, state.RemainingSeconds);

        now = now.AddSeconds(1);
        Assert.Equal(0, state.RemainingSeconds);
        Assert.False(state.IsAccepting);
        Assert.Equal(LanPairVerdict.CodeExpired, state.TryConsume(code, "192.168.1.9"));
        Assert.NotEqual(code, state.Arm());
    }

    [Fact]
    public void A_source_that_guessed_its_way_through_the_budget_is_answered_no_more()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LanPairingState(() => now);
        var code = state.Arm();
        for (var attempt = 1; attempt <= LanProtocol.MaximumFailedAttempts; attempt++)
            Assert.Equal(LanPairVerdict.BadCode, state.TryConsume("ZZZZZZ", "192.168.1.9"));

        // The armed code is still live for everybody else; what the budget cost is this one source.
        Assert.Equal(LanPairVerdict.RateLimited, state.TryConsume(code, "192.168.1.9"));
        Assert.Equal(LanPairVerdict.Accepted, state.TryConsume(code, "192.168.1.10"));
    }

    [Fact]
    public void The_attempt_budget_is_a_window_and_a_quiet_source_gets_to_try_again()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LanPairingState(() => now);
        var code = state.Arm();
        for (var attempt = 0; attempt < LanProtocol.MaximumFailedAttempts; attempt++)
            state.TryConsume("ZZZZZZ", "192.168.1.9");
        Assert.Equal(LanPairVerdict.RateLimited, state.TryConsume(code, "192.168.1.9"));

        now = now.AddSeconds(LanProtocol.AttemptWindowSeconds + 1);
        Assert.Equal(LanPairVerdict.BadCode, state.TryConsume("ZZZZZZ", "192.168.1.9"));
    }

    [Fact]
    public void A_malformed_attempt_never_reads_as_the_rate_limit_even_after_a_hundred_of_them()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LanPairingState(() => now);
        state.Arm();
        // Junk is not guessing: it never reaches the budget, and the answer the user needs is "wrong code".
        for (var attempt = 0; attempt < 100; attempt++)
            Assert.Equal(LanPairVerdict.BadCode, state.TryConsume("!!!!", "192.168.1.9"));
        Assert.True(state.IsAccepting);
    }

    [Fact]
    public void Invalidating_leaves_nothing_answerable_until_a_new_code_is_armed()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LanPairingState(() => now);
        var code = state.Arm();
        state.Invalidate();
        Assert.False(state.IsAccepting);
        Assert.Equal(0, state.RemainingSeconds);
        Assert.Equal(LanPairVerdict.CodeExpired, state.TryConsume(code, "192.168.1.9"));
        Assert.NotEqual("", state.Arm());
    }
}
