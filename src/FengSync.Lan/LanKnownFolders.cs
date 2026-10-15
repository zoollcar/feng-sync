using System.Runtime.InteropServices;

namespace FengSync.Lan;

/// <summary>
/// The folder an inbound file goes to when the user never chose one.
/// </summary>
/// <remarks>
/// <para>
/// Downloads rather than the application's own data directory, because a file somebody sent you is something
/// you are meant to go and use, and a copy that lands under <c>AppData</c> is a file that will never be found
/// again. This is also the one directory in this feature that is not user-visible until a transfer arrives.
/// </para>
/// <para>
/// Existence is deliberately not checked here. The caller already has to answer a different question — what to
/// do when the name the user <em>did</em> choose has since been deleted, which is a dialog rather than a
/// silent substitution — and a resolver that quietly returned a different directory would take that decision
/// away from it. What is asked instead is which folder Windows calls Downloads on this machine, in this user's
/// account, with whatever redirection a domain policy has applied.
/// </para>
/// </remarks>
public static class LanKnownFolders
{
    /// <summary>
    /// The known-folder id for Downloads, including its braces, as <c>SHGetKnownFolderPath</c> takes it.
    /// </summary>
    /// <remarks>
    /// This string is the whole feature, and it is not checkable by reading: the id one character away resolves
    /// to a machine folder rather than to the user's, and it resolves *successfully*, so nothing downstream
    /// complains. <see cref="FengSync.Tests.LanKnownFoldersTests"/> therefore asks the shell itself which id
    /// this machine names Downloads and compares, instead of trusting the literal.
    /// </remarks>
    public const string DownloadsFolderId = "{374DE290-123F-4565-9164-39C4925E467B}";

    /// <summary>
    /// The known-folder id for the user's profile directory, which is where Downloads lives when it has never
    /// been redirected.
    /// </summary>
    /// <remarks>
    /// An id that is not in the shell's table is not a failed lookup but a silent one: it answers nothing, so the
    /// fallback below would give up even on a machine with a perfectly good profile.
    /// </remarks>
    public const string UserProfileFolderId = "{5E6C858F-0E22-4760-9AFE-EA3317B67173}";

    /// <summary>
    /// The directory to receive into, or <see langword="null"/> when this machine cannot name one.
    /// </summary>
    /// <param name="readKnownFolder">
    /// How a folder id becomes a path. A parameter because the answer is a machine-level fact: a test that
    /// resolves the real Downloads folder is a test that writes into the user's Downloads folder, and the
    /// fallback chain below could only ever be reached on a machine that was already missing something.
    /// </param>
    public static string? Downloads(Func<string, string?>? readKnownFolder = null)
    {
        var read = readKnownFolder ?? ReadFromShell;

        var redirected = read(DownloadsFolderId);
        if (!string.IsNullOrWhiteSpace(redirected)) return redirected.Trim();

        // A profile with no Downloads entry yet is still a profile, and the sink creates what it needs before
        // it writes. Both halves of this answer can be missing: Windows hands back an empty string for a
        // profile it cannot resolve, and then there is genuinely nowhere to put a file.
        var profile = read(UserProfileFolderId);
        if (string.IsNullOrWhiteSpace(profile)) return null;
        return Path.Combine(profile.Trim(), "Downloads");
    }

    /// <summary>
    /// Asks the shell directly. Returns <see langword="null"/> rather than throwing for any failure, because a
    /// folder this side cannot name is a reason to fall back, not a reason the window cannot open.
    /// </summary>
    private static string? ReadFromShell(string folderId)
    {
        if (!Guid.TryParse(folderId, out var id)) return null;
        try
        {
            // Flag 0: resolve the configured location, and do not create it on the way past. Creating a
            // directory is the sink's decision to make, in a path the user has agreed to.
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) != 0) return null;
            try
            {
                return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                // The shell allocated this with CoTaskMemAlloc, so it is not the CLR's to free, and a window
                // that opens twice a day would leak a path both times.
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, int flags, IntPtr token, out IntPtr path);
}
