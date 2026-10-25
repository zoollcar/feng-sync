using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The one place in this protocol where a person answers instead of a credential, and therefore the one place with
/// rules about how long a question may live and how many of them one machine may be asked.
/// </summary>
/// <remarks>
/// Driven with an explicit clock rather than by waiting: every rule here is about a deadline, and a suite that
/// slept ninety seconds to prove one would be a suite nobody runs. Nothing in these cases touches a socket or a
/// file — the board holds a question in memory and writes nothing, which is the property the last case pins.
/// </remarks>
public sealed class LanInviteBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private const string Self = "11111111111111111111111111111111";
    private const string Other = "22222222222222222222222222222222";
    private const string Caller = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    /// <summary>A credential of the shape the store mints: 32 random bytes, hex, which is 64 characters.</summary>
    private const string Grant = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>An id of the shape this protocol mints: hex, eight characters, so the form check accepts it.</summary>
    private static string Id(int number) => $"abc{number:00000}";

    [Fact]
    public void An_invitation_is_held_for_a_person_rather_than_answered_by_the_request()
    {
        var board = new LanInviteBoard();

        var opened = board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        Assert.Equal(LanRefusal.None, opened.Refusal);
        Assert.Equal(LanInviteDecision.Pending, opened.Decision);
        Assert.Equal(1, board.PendingCount);
        // Held for ninety seconds and not a moment longer, which is the whole of the promise to the caller waiting.
        Assert.Equal(Now.AddSeconds(LanProtocol.InviteLifetimeSeconds), opened.Invite!.ExpiresUtc);
    }

    [Fact]
    public async Task A_person_answering_settles_the_question_and_a_second_answer_does_not_decide_again()
    {
        var board = new LanInviteBoard();
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        var first = await board.AnswerAsync(Id(1), accept: true, Now.AddSeconds(5));
        var second = await board.AnswerAsync(Id(1), accept: false, Now.AddSeconds(6));

        Assert.Equal(LanInviteDecision.Accepted, first.Decision);
        Assert.True(first.Fresh);
        // A double click is not a second decision, and a caller that writes credentials keys on Fresh so one
        // membership cannot end up with two tokens.
        Assert.Equal(LanInviteDecision.Accepted, second.Decision);
        Assert.False(second.Fresh);
        Assert.Equal(LanRefusal.None, second.Refusal);
    }

    [Fact]
    public async Task An_invitation_nobody_answers_ages_out_and_can_no_longer_be_joined()
    {
        var board = new LanInviteBoard();
        var lapsed = new List<string>();
        board.Lapsed += invite => lapsed.Add(invite.InviteId);
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        var remaining = board.Sweep(Now.AddSeconds(LanProtocol.InviteLifetimeSeconds + 1));
        var late = await board.AnswerAsync(Id(1), accept: true, Now.AddSeconds(LanProtocol.InviteLifetimeSeconds + 2));

        Assert.Empty(remaining);
        Assert.Equal(new[] { Id(1) }, lapsed);
        // A yes after the deadline is refused rather than honoured: the inviter stopped waiting, so the credential
        // it would have needed has already been released on its own side.
        Assert.Equal(LanRefusal.InviteExpired, late.Refusal);
        Assert.Equal(LanInviteDecision.Expired, late.Decision);
        Assert.False(late.Fresh);
    }

    [Fact]
    public async Task A_caller_waiting_is_told_what_the_person_decided()
    {
        var board = new LanInviteBoard();
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        var waiting = board.AwaitAsync(Id(1), Now);
        await board.AnswerAsync(Id(1), accept: true, Now.AddSeconds(1));

        Assert.Equal(LanInviteDecision.Accepted, await waiting);
    }

    [Fact]
    public async Task A_waiter_is_not_told_yes_before_the_work_that_yes_implies_has_finished()
    {
        var board = new LanInviteBoard();
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);
        var committed = false;

        var waiting = board.AwaitAsync(Id(1), Now);
        await board.AnswerAsync(Id(1), accept: true, Now.AddSeconds(1), _ =>
        {
            committed = true;
            return Task.CompletedTask;
        });

        // The order is the property: a caller that heard yes first would store a credential that does not exist yet.
        Assert.Equal(LanInviteDecision.Accepted, await waiting);
        Assert.True(committed);
    }

    [Fact]
    public async Task A_yes_that_could_not_be_carried_out_is_reported_as_a_no()
    {
        var board = new LanInviteBoard();
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        var waiting = board.AwaitAsync(Id(1), Now);
        await Assert.ThrowsAsync<IOException>(() => board.AnswerAsync(Id(1), accept: true, Now.AddSeconds(1),
            _ => throw new IOException("the credential file is locked")));

        // A caller told yes would store a token that does not exist; a caller told no releases the one it minted.
        Assert.Equal(LanInviteDecision.Declined, await waiting);
    }

    [Fact]
    public void The_same_id_twice_from_one_machine_is_one_question()
    {
        var board = new LanInviteBoard();

        var first = board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);
        var again = board.Open(Request(Id(1)), "10.0.0.5", 59871, Now.AddSeconds(3));

        Assert.Equal(LanRefusal.None, again.Refusal);
        Assert.Equal(LanInviteDecision.Pending, again.Decision);
        Assert.Equal(first.Invite!.ReceivedUtc, again.Invite!.ReceivedUtc);
        Assert.Equal(1, board.PendingCount);
        // A retry must not spend a second slot in the rate limit either, or a caller whose own timeout fired would
        // lock itself out of the two attempts it has left.
        Assert.Equal(LanProtocol.MaximumInvitesPerSource - 1, Remaining(board, Caller, Now.AddSeconds(4)));
    }

    [Fact]
    public void The_same_id_from_another_machine_is_a_collision_rather_than_a_retry()
    {
        var board = new LanInviteBoard();
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        var other = board.Open(Request(Id(1), deviceId: Other), "10.0.0.6", 59871, Now.AddSeconds(1));

        Assert.Equal(LanRefusal.InviteConflict, other.Refusal);
        Assert.Null(other.Invite);
    }

    [Fact]
    public void One_machine_may_only_open_three_invitations_a_minute()
    {
        var board = new LanInviteBoard();
        for (var index = 0; index < LanProtocol.MaximumInvitesPerSource; index++)
            Assert.True(board.Open(Request(Id(index)), "10.0.0.5", 59871, Now.AddSeconds(index)).Opened);

        var fourth = board.Open(Request(Id(90)), "10.0.0.5", 59871, Now.AddSeconds(4));
        // …and the window really is a window: a minute later the same machine may ask again.
        var later = board.Open(Request(Id(91)), "10.0.0.5", 59871, Now.AddSeconds(LanProtocol.InviteWindowSeconds + 5));

        Assert.Equal(LanRefusal.RateLimited, fourth.Refusal);
        Assert.True(later.Opened);
    }

    [Fact]
    public void No_more_than_eight_questions_are_held_at_once()
    {
        var board = new LanInviteBoard();
        for (var index = 0; index < LanProtocol.MaximumPendingInvites; index++)
        {
            // A different source each time, so the cap being measured is the global one rather than the per-source
            // one — the per-source limit is a speed bump and this is the wall behind it.
            var source = index.ToString("x8") + new string('0', 24);
            Assert.True(board.Open(Request(Id(index), deviceId: source), "10.0.0.5", 59871, Now).Opened);
        }

        var extra = board.Open(Request(Id(99), deviceId: "99999999999999999999999999999999"), "10.0.0.5", 59871, Now);

        Assert.Equal(LanRefusal.InviteConflict, extra.Refusal);
        Assert.Equal(LanProtocol.MaximumPendingInvites, board.PendingCount);
    }

    [Fact]
    public void Only_the_machine_that_asked_may_take_its_question_back()
    {
        var board = new LanInviteBoard();
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        Assert.False(board.Withdraw(Id(1), Other, Now.AddSeconds(1)));
        Assert.Equal(1, board.PendingCount);
        Assert.True(board.Withdraw(Id(1), Caller, Now.AddSeconds(2)));
        Assert.Equal(0, board.PendingCount);
    }

    [Fact]
    public async Task A_withdrawn_invitation_can_no_longer_be_answered()
    {
        var board = new LanInviteBoard();
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);
        Assert.True(board.Withdraw(Id(1), Caller, Now.AddSeconds(1)));

        var late = await board.AnswerAsync(Id(1), accept: true, Now.AddSeconds(2));

        // The row is still findable — that is what lets a click on it be answered with what really happened — but
        // it can never become a yes.
        Assert.Equal(LanInviteDecision.Withdrawn, late.Decision);
        Assert.False(late.Fresh);
        Assert.NotNull(late.Invite);
    }

    [Fact]
    public void A_question_is_only_ever_shown_to_the_device_it_names()
    {
        var board = new LanInviteBoard();
        board.Open(Request(Id(1), target: Self), "10.0.0.5", 59871, Now);
        board.Open(Request(Id(2), target: Other), "10.0.0.5", 59871, Now.AddSeconds(1));

        Assert.Equal(new[] { Id(1) }, board.For(Self, Now).Select(invite => invite.InviteId));
        Assert.Equal(new[] { Id(2) }, board.For(Other, Now).Select(invite => invite.InviteId));
    }

    [Theory]
    [InlineData("", Other, Grant)]            // no id
    [InlineData("nope", Other, Grant)]        // an id no build mints
    [InlineData("abc00001", "zz", Grant)]     // not a device id
    [InlineData("abc00001", Other, "abc")]    // not a token: an invitation with no credential is unusable
    public void An_invitation_that_is_not_well_formed_is_refused_before_anything_is_held(string id, string deviceId, string grant)
    {
        var board = new LanInviteBoard();
        var request = new LanInviteRequest(id, deviceId, "客厅电脑", LanProtocol.KindWindows, 59871, grant, Self);

        var opened = board.Open(request, "10.0.0.5", 59871, Now);

        Assert.Equal(LanRefusal.BadRequest, opened.Refusal);
        Assert.Equal(0, board.PendingCount);
    }

    [Fact]
    public void An_invitation_with_no_target_is_refused_rather_than_guessed_at()
    {
        var board = new LanInviteBoard();
        var request = new LanInviteRequest(Id(1), Other, "客厅电脑", LanProtocol.KindWindows, 59871, Grant, null);

        // "Whoever is reading this" is a fact only the listener knows, and it fills the hole in before the board is
        // asked. A board that guessed would file the question under nothing and no window would ever show it.
        Assert.Equal(LanRefusal.BadRequest, board.Open(request, "10.0.0.5", 59871, Now).Refusal);
    }

    [Fact]
    public async Task Forgetting_a_device_takes_its_questions_with_it()
    {
        var board = new LanInviteBoard();
        var lapsed = new List<string>();
        board.Lapsed += invite => lapsed.Add(invite.InviteId);
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        var dropped = board.Forget(Caller, Now.AddSeconds(1));
        var late = await board.AnswerAsync(Id(1), accept: true, Now.AddSeconds(2));

        Assert.Single(dropped);
        Assert.Equal(new[] { Id(1) }, lapsed);
        // 取消信任 answered the question, so a click on a stale row cannot put the row straight back.
        Assert.Equal(LanInviteDecision.Withdrawn, late.Decision);
        Assert.False(late.Fresh);
    }

    [Fact]
    public void Closing_the_board_answers_every_question_with_a_no_and_says_nothing_about_it()
    {
        var board = new LanInviteBoard();
        var narrated = 0;
        board.Lapsed += _ => narrated++;
        board.Open(Request(Id(1)), "10.0.0.5", 59871, Now);

        board.Clear();

        Assert.Equal(0, board.PendingCount);
        // A window closing is not a lapse worth narrating — the person who was going to answer has gone.
        Assert.Equal(0, narrated);
    }

    /// <summary>How many more questions one source may still open, measured by asking until it is refused.</summary>
    private static int Remaining(LanInviteBoard board, string source, DateTimeOffset now)
    {
        var allowed = 0;
        for (var index = 0; index < 10; index++)
        {
            if (!board.Open(Request(Id(700 + index), deviceId: source), "10.0.0.5", 59871, now).Opened) break;
            allowed++;
        }

        return allowed;
    }

    private static LanInviteRequest Request(string inviteId, string deviceId = Caller, string? target = Self) =>
        new(inviteId, deviceId, "客厅电脑", LanProtocol.KindWindows, 59871, Grant, target);
}
