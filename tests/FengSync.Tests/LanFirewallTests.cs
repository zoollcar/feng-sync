using System.ComponentModel;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers the firewall plan, the read that answers without elevating, and what each outcome means to the person
/// who is trying to send a file to a phone.
/// </summary>
/// <remarks>
/// <para>
/// No case here runs <c>netsh</c>, and that is the point of the injected runner: the promise this code makes is
/// that a configured machine never sees a consent dialog, which cannot be shown by starting one. The assertions
/// are about which calls were made, in what order, and with what arguments — the arguments being the part that
/// would silently leave a port closed.
/// </para>
/// <para>
/// The outcome states are tested as outcomes rather than as exit codes, because the window turns each one into a
/// sentence, and a declined dialog has to stay a declined dialog rather than becoming a crash.
/// </para>
/// </remarks>
public sealed class LanFirewallTests
{
    private const int Port = 59871;

    [Fact]
    public void The_plan_needs_both_protocols_on_the_number_the_listener_uses()
    {
        var plan = LanFirewall.PlanFor(Port);

        // Discovery is UDP and transfer is TCP on one port, and a firewall that allows only one half shows up as
        // "the computers never find each other" while every manual address works.
        Assert.Equal(2, plan.Count);
        Assert.Equal(["TCP", "UDP"], plan.Select(rule => rule.Protocol));
        Assert.All(plan, rule =>
        {
            Assert.Equal(Port, rule.Port);
            Assert.StartsWith(LanFirewall.RuleNamePrefix, rule.Name, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void A_port_no_rule_could_name_is_refused_before_anything_runs(int port)
    {
        // The rule is a port rule, so a number netsh would reject has to be caught while it is still a plan.
        Assert.Throws<ArgumentOutOfRangeException>(() => LanFirewall.PlanFor(port));
    }

    [Fact]
    public async Task A_check_asks_for_nothing_it_could_read_instead()
    {
        var netsh = new FakeNetsh();
        foreach (var rule in LanFirewall.PlanFor(Port)) netsh.Present(rule);

        Assert.True(await LanFirewall.InspectAsync(Port, netsh.Runner));
        Assert.Equal(2, netsh.Calls.Count);
        // Not one of these calls may carry the elevation verb: a prompt on every window opening would teach the
        // user to click through it unread, which is worse than a phone that cannot connect.
        Assert.All(netsh.Calls, call => Assert.False(call.Elevated));
        Assert.All(netsh.Calls, call => Assert.Contains("show rule", call.Arguments, StringComparison.Ordinal));
    }

    [Theory]
    // The name is echoed in the machine's own wording, so the name is the only part worth matching.
    [InlineData("Rule Name:                              Feng Sync LAN transfer (TCP)", "Feng Sync LAN transfer (TCP)", true)]
    [InlineData("规则名称:                               Feng Sync LAN transfer (TCP)", "Feng Sync LAN transfer (TCP)", true)]
    [InlineData("No rules match the specified criteria.", "Feng Sync LAN transfer (TCP)", false)]
    [InlineData("没有与指定标准相匹配的规则。", "Feng Sync LAN transfer (TCP)", false)]
    [InlineData("", "Feng Sync LAN transfer (TCP)", false)]
    [InlineData(null, "Feng Sync LAN transfer (TCP)", false)]
    [InlineData("Rule Name:  feng sync lan transfer (tcp)", "Feng Sync LAN transfer (TCP)", true)]
    public void A_rule_is_found_by_its_own_text_rather_than_by_a_sentence(
        string? output, string ruleName, bool expected)
    {
        Assert.Equal(expected, LanFirewall.ReportsRule(output, ruleName));
    }

    [Fact]
    public void An_add_rule_call_names_the_port_it_opens_and_the_direction_it_opens_it_in()
    {
        var rule = LanFirewall.PlanFor(Port)[0];
        var arguments = LanFirewall.AddArguments(rule);

        // The quoting is load-bearing in a way an argument list would hide: the value starts after the quote,
        // which is the form netsh's own parser accepts for a name holding a space.
        Assert.Contains("name=\"" + rule.Name + "\"", arguments, StringComparison.Ordinal);
        Assert.Contains("dir=in", arguments, StringComparison.Ordinal);
        Assert.Contains("action=allow", arguments, StringComparison.Ordinal);
        Assert.Contains("profile=any", arguments, StringComparison.Ordinal);
        Assert.Contains("protocol=TCP", arguments, StringComparison.Ordinal);
        Assert.Contains("localport=" + Port, arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_rule_is_the_only_reason_elevation_is_ever_offered()
    {
        var netsh = new FakeNetsh();
        netsh.Present(LanFirewall.PlanFor(Port)[0]);

        Assert.Equal(LanFirewallOutcome.Added, await LanFirewall.EnsureAsync(Port, netsh.Runner));
        Assert.Equal(2, netsh.Calls.Count(call => !call.Elevated));
        var elevation = Assert.Single(netsh.Calls, call => call.Elevated);
        // Both halves were checked, and only the missing one was written — and it is the UDP half, because TCP
        // was already open. That order is what makes 重复点击 harmless.
        Assert.Contains("UDP", elevation.Arguments, StringComparison.Ordinal);
        Assert.Equal(1, netsh.OpenCount);
    }

    [Fact]
    public async Task An_already_open_machine_is_asked_of_nobody()
    {
        var netsh = new FakeNetsh();
        foreach (var rule in LanFirewall.PlanFor(Port)) netsh.Present(rule);

        Assert.Equal(LanFirewallOutcome.AlreadyAllowed, await LanFirewall.EnsureAsync(Port, netsh.Runner));
        Assert.DoesNotContain(netsh.Calls, call => call.Elevated);
    }

    [Theory]
    [InlineData(LanFirewall.ErrorCancelled, LanFirewallOutcome.DeclinedByUser)]
    [InlineData(LanFirewall.ErrorAccessDenied, LanFirewallOutcome.FailedElevated)]
    [InlineData(87, LanFirewallOutcome.FailedUnknown)]
    [InlineData(1, LanFirewallOutcome.FailedUnknown)]
    [InlineData(LanFirewall.DidNotStart, LanFirewallOutcome.FailedUnknown)]
    public void An_answer_from_the_add_describes_the_firewall_not_the_process(int exitCode, LanFirewallOutcome expected)
    {
        // Windows reports a closed consent dialog as 1223 and nothing else, so any other number has to reach the
        // state that says "this side does not know" rather than blaming the user's click.
        Assert.Equal(expected, LanFirewall.Classify(new LanProcessOutcome(exitCode, "")));
    }

    [Fact]
    public async Task A_declined_dialog_is_a_choice_some_person_made_and_not_a_crash()
    {
        var netsh = new FakeNetsh();
        netsh.Refuse(LanFirewall.PlanFor(Port), LanFirewall.ErrorCancelled);

        // The window must still offer the button, and it must not say the firewall is broken.
        Assert.Equal(LanFirewallOutcome.DeclinedByUser, await LanFirewall.EnsureAsync(Port, netsh.Runner));
    }

    [Fact]
    public async Task One_declined_rule_outweighs_one_added_one()
    {
        var netsh = new FakeNetsh();
        netsh.Refuse([LanFirewall.PlanFor(Port)[1]], LanFirewall.ErrorCancelled);

        // Half the plan open means the phone still cannot connect. Reporting "Added" would send the user looking
        // for a different cause, even though the TCP half really did get written.
        Assert.Equal(LanFirewallOutcome.DeclinedByUser, await LanFirewall.EnsureAsync(Port, netsh.Runner));
        Assert.Equal(1, netsh.OpenCount);
    }

    [Fact]
    public async Task A_firewall_that_cannot_be_read_is_never_written_to_blind()
    {
        var netsh = new FakeNetsh { ShowFails = true };

        Assert.Equal(LanFirewallOutcome.FailedUnknown, await LanFirewall.EnsureAsync(Port, netsh.Runner));
        Assert.DoesNotContain(netsh.Calls, call => call.Elevated);
        // A check that cannot run is not a green light either: the button is offered, and nothing is claimed.
        Assert.False(await LanFirewall.InspectAsync(Port, netsh.Runner));
    }

    /// <summary>
    /// A stand-in for <c>netsh</c> that keeps only the facts this code can see: what was asked, and which rules
    /// the machine already had.
    /// </summary>
    private sealed class FakeNetsh
    {
        private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _refusals = new(StringComparer.OrdinalIgnoreCase);

        public List<LanFirewallCall> Calls { get; } = [];
        public bool ShowFails { get; init; }

        /// <summary>How many rules this run actually wrote.</summary>
        public int OpenCount { get; private set; }

        public void Present(LanFirewallRule rule) => _present.Add(rule.Name);

        public void Refuse(IEnumerable<LanFirewallRule> rules, int exitCode)
        {
            foreach (var rule in rules) _refusals[rule.Name] = exitCode;
        }

        public LanProcessRunner Runner => (call, _) =>
        {
            Calls.Add(call);
            if (ShowFails && !call.Elevated) throw new Win32Exception(2, "netsh.exe 不存在。");

            var name = RuleNameOf(call.Arguments);
            if (!call.Elevated)
            {
                var output = _present.Contains(name)
                    ? "Rule Name:                              " + name + "\n"
                    : "No rules match the specified criteria.\n";
                return Task.FromResult(new LanProcessOutcome(0, output));
            }

            var exitCode = _refusals.TryGetValue(name, out var refused) ? refused : 0;
            if (exitCode != 0) return Task.FromResult(new LanProcessOutcome(exitCode, ""));

            _present.Add(name);
            OpenCount++;
            return Task.FromResult(new LanProcessOutcome(0, "Ok.\n"));
        };

        /// <summary>
        /// Reads the rule out of the command line the way <c>netsh</c> would. A plan that stopped quoting its name
        /// fails here rather than in front of a firewall.
        /// </summary>
        private static string RuleNameOf(string arguments)
        {
            const string marker = "name=\"";
            var start = arguments.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, $"参数里没有带引号的规则名：{arguments}");
            var value = arguments[(start + marker.Length)..];
            var end = value.IndexOf('"');
            Assert.True(end > 0, $"规则名没有收尾的引号：{arguments}");
            return value[..end];
        }
    }
}
