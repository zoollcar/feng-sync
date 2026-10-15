using System.Diagnostics;
using System.Globalization;

namespace FengSync.Lan;

/// <summary>One inbound allow rule this feature needs, in the form <c>netsh</c> names it.</summary>
public sealed record LanFirewallRule(string Name, string Protocol, int Port);

/// <summary>
/// A <c>netsh</c> run to perform: the arguments, and whether this one has to ask Windows for elevation.
/// </summary>
/// <remarks>
/// A record rather than a command line, because the two halves of this job are not the same kind of call. A
/// check must not elevate — see <see cref="LanFirewall.InspectAsync"/> for what happens when it does — and an
/// add cannot be done without it.
/// </remarks>
public sealed record LanFirewallCall(string Arguments, bool Elevated);

/// <summary>What a process answered. <paramref name="Output"/> is empty for a run that could not capture any.</summary>
public sealed record LanProcessOutcome(int ExitCode, string Output);

/// <summary>How a <c>netsh</c> call was made. Injected, so the judgements below are testable without a UAC prompt.</summary>
public delegate Task<LanProcessOutcome> LanProcessRunner(LanFirewallCall call, CancellationToken ct);

/// <summary>
/// Why a phone still cannot connect, in the states a window can answer.
/// </summary>
/// <remarks>
/// Five named outcomes rather than a bool, because the five need five different sentences — and one of them is
/// not a failure at all.
/// </remarks>
public enum LanFirewallOutcome
{
    /// <summary>Every rule the plan needs is already there, so nothing was asked of the user.</summary>
    AlreadyAllowed,

    /// <summary>The rules were missing and are now present.</summary>
    Added,

    /// <summary>
    /// The user said no to the elevation dialog. Deliberately not a failure: the button stays there to press
    /// again, and a message that reads like a crash would send them looking for something to fix.
    /// </summary>
    DeclinedByUser,

    /// <summary>The call was refused for lack of rights, without a dialog anybody answered.</summary>
    FailedElevated,

    /// <summary>Something this side cannot name. Reported as itself rather than as a firewall problem.</summary>
    FailedUnknown,
}

/// <summary>
/// The Windows firewall rules that let a peer reach this machine, and the smallest way to ask for them.
/// </summary>
/// <remarks>
/// <para>
/// Port rules, not a program rule: a rule that names <c>FengSync.exe</c> is bound to wherever this build
/// happens to sit and cannot be checked after an upgrade, while a rule that names a fixed port is verifiable
/// before anything is elevated — which is the only way a second click can be harmless.
/// </para>
/// <para>
/// Two rules, TCP and UDP, because the transfer and the discovery are different protocols on one number, and a
/// firewall that allows one does not allow the other. An unlisted UDP half shows up as "the phones never find
/// each other" while everything else works, which is the hardest of these bugs to describe from a support call.
/// </para>
/// <para>
/// Rule names are ASCII on purpose. A redirected console writes the codepage the child chose, so a name in
/// Chinese would be read back as mojibake and the check would answer "missing" — and then add a second rule
/// beside the one that was there. An ASCII name decodes the same under every codepage, so the presence test
/// holds regardless of what language this machine speaks.
/// </para>
/// </remarks>
public static class LanFirewall
{
    /// <summary>The shared prefix of both rules. It is what <c>show rule</c> looks for, so changing it strands existing rules.</summary>
    public const string RuleNamePrefix = "Feng Sync LAN transfer";

    /// <summary>The Windows error for a consent dialog somebody closed. Not a failure, and never reported as one.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>The Windows error for a call that needed rights it did not have.</summary>
    public const int ErrorAccessDenied = 5;

    /// <summary>An exit code no process answers with, used when the process never started.</summary>
    public const int DidNotStart = -1;

    /// <summary>
    /// Every rule this feature needs on a given port, in the order it asks for them.
    /// </summary>
    /// <remarks>
    /// A pure plan rather than a side effect, because "what would you do" must be answerable on a machine where
    /// the answer is already yes.
    /// </remarks>
    public static IReadOnlyList<LanFirewallRule> PlanFor(int port)
    {
        if (port is < LanOptions.MinimumPort or > LanOptions.MaximumPort)
            throw new ArgumentOutOfRangeException(nameof(port), $"局域网快传端口必须介于 {LanOptions.MinimumPort} 和 {LanOptions.MaximumPort} 之间。");
        return
        [
            new LanFirewallRule($"{RuleNamePrefix} (TCP)", "TCP", port),
            new LanFirewallRule($"{RuleNamePrefix} (UDP)", "UDP", port),
        ];
    }

    /// <summary>Reads one rule's presence. No elevation, no change, no dialog.</summary>
    public static string ShowArguments(LanFirewallRule rule) =>
        "advfirewall firewall show rule name=\"" + rule.Name + "\"";

    /// <summary>
    /// Writes one rule: inbound, allowed, every profile, one port.
    /// </summary>
    /// <remarks>
    /// <c>profile=any</c> because a home network is private and an office one is domain, and a feature that
    /// works after the user carries the laptop somewhere else is not a feature. Nothing here takes a value from
    /// a caller except a validated port and these two constant names, so the quoting is not a boundary.
    /// </remarks>
    public static string AddArguments(LanFirewallRule rule) =>
        "advfirewall firewall add rule name=\"" + rule.Name
        + "\" dir=in action=allow enable=yes profile=any protocol=" + rule.Protocol
        + " localport=" + rule.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a <c>show rule</c> answer names this rule.
    /// </summary>
    /// <remarks>
    /// Matched on the rule's own text rather than on an exit code or a header, because <c>netsh</c> answers a
    /// missing rule with exit code 0 and a sentence in the machine's language ("没有与指定标准相匹配的规则。"
    /// here), so the only locale-independent fact is the name it echoes back when the rule exists.
    /// </remarks>
    public static bool ReportsRule(string? output, string ruleName) =>
        !string.IsNullOrEmpty(output) && output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);

    /// <summary>What an add attempt means.</summary>
    public static LanFirewallOutcome Classify(LanProcessOutcome add) => add.ExitCode switch
    {
        0 => LanFirewallOutcome.Added,
        ErrorCancelled => LanFirewallOutcome.DeclinedByUser,
        ErrorAccessDenied => LanFirewallOutcome.FailedElevated,
        _ => LanFirewallOutcome.FailedUnknown,
    };

    /// <summary>
    /// Whether every rule the plan needs is already present.
    /// </summary>
    /// <remarks>
    /// This is the call the window makes when it opens, and the reason it exists separately: an elevation
    /// prompt on every launch would teach the user to click through it without reading, which is worse for
    /// them than a phone that cannot connect. A check that cannot run answers false rather than throwing, so
    /// the button is offered either way.
    /// </remarks>
    public static async Task<bool> InspectAsync(int port, LanProcessRunner? runner = null, CancellationToken ct = default)
    {
        var run = runner ?? RunNetshAsync;
        foreach (var rule in PlanFor(port))
        {
            LanProcessOutcome outcome;
            try
            {
                outcome = await run(new LanFirewallCall(ShowArguments(rule), false), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            // A failed read is not a missing rule as far as the user is concerned, but it is not a green light
            // either: the honest answer is "not known to be allowed", which offers the same button.
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }

            if (outcome.ExitCode != 0 || !ReportsRule(outcome.Output, rule.Name)) return false;
        }

        return true;
    }

    /// <summary>
    /// Makes the firewall allow this port, elevating only when a rule is genuinely missing.
    /// </summary>
    /// <remarks>
    /// The order of the two calls is the whole design: check first, and a machine that is already configured
    /// never sees a dialog. The result is the worst thing that happened, because one added rule and one
    /// declined one leave the phone unable to connect — and a window that reported success would send the user
    /// looking for a different cause.
    /// </remarks>
    public static async Task<LanFirewallOutcome> EnsureAsync(int port, LanProcessRunner? runner = null, CancellationToken ct = default)
    {
        var run = runner ?? RunNetshAsync;
        var result = LanFirewallOutcome.AlreadyAllowed;
        foreach (var rule in PlanFor(port))
        {
            LanProcessOutcome shown;
            try
            {
                shown = await run(new LanFirewallCall(ShowArguments(rule), Elevated: false), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Nothing could be read, so nothing can be verified. Say so rather than elevating blind.
                return Worse(result, LanFirewallOutcome.FailedUnknown);
            }

            if (shown.ExitCode == 0 && ReportsRule(shown.Output, rule.Name)) continue;

            var added = await run(new LanFirewallCall(AddArguments(rule), Elevated: true), ct).ConfigureAwait(false);
            result = Worse(result, Classify(added));
        }

        return result;
    }

    /// <summary>
    /// Runs <c>netsh</c> for real.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes, because Windows only offers the elevation verb through the shell, and the shell gives back no
    /// output. A check is redirected and windowless; an add goes through <c>runas</c> and is not. Trying to
    /// capture an elevated run would need a second process and a temp file — for a message nobody reads.
    /// </para>
    /// <para>
    /// A closed consent dialog arrives as a <see cref="System.ComponentModel.Win32Exception"/> with this code,
    /// not as an exit code, and is turned into <see cref="ErrorCancelled"/> so <see cref="Classify"/> can name
    /// it. Anything else that stops the process from starting at all reports
    /// <see cref="DidNotStart"/>, which classifies as unknown rather than pretending the firewall said no.
    /// </para>
    /// </remarks>
    public static async Task<LanProcessOutcome> RunNetshAsync(LanFirewallCall call, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = call.Arguments,
            UseShellExecute = call.Elevated,
            RedirectStandardOutput = !call.Elevated,
            RedirectStandardError = !call.Elevated,
            CreateNoWindow = !call.Elevated,
        };

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("netsh.exe 无法启动。");
            var output = "";
            if (!call.Elevated)
            {
                // Both streams are drained before waiting, because a child that fills an unread buffer blocks
                // on its own write and this call would never end.
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new(process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new LanProcessOutcome(ErrorCancelled, "");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new LanProcessOutcome(DidNotStart, ex.Message);
        }
    }

    private static LanFirewallOutcome Worse(LanFirewallOutcome left, LanFirewallOutcome right) =>
        Rank(left) >= Rank(right) ? left : right;

    private static int Rank(LanFirewallOutcome outcome) => outcome switch
    {
        LanFirewallOutcome.AlreadyAllowed => 0,
        LanFirewallOutcome.Added => 1,
        LanFirewallOutcome.DeclinedByUser => 2,
        LanFirewallOutcome.FailedElevated => 3,
        _ => 4,
    };
}
