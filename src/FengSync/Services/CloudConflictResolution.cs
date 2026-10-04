using FengSync.Core.Configuration;

namespace FengSync.Services;

/// <summary>
/// What to do about one file whose destination is already taken. The three answers are the ones a user can
/// give without reading a manual: replace it, keep both by renaming the incoming file, or leave the
/// destination alone. Only <see cref="KeepBoth"/> needs a name computed against the listing, which is why the
/// choice and the placement are decided together rather than in the window.
/// </summary>
public enum CloudConflictChoice { Overwrite, KeepBoth, Skip }

/// <summary>
/// One answer given to the conflict prompt, and whether the user wants it repeated for the files still
/// waiting. Applying an answer to the rest is what keeps a dropped tree from asking once per file — the same
/// promise a saved policy makes, given at the moment of the conflict instead of in the settings.
/// </summary>
public sealed record CloudConflictAnswer(CloudConflictChoice Choice, bool ApplyToAll);

/// <summary>
/// Where one incoming file actually goes. <see cref="Destination"/> differs from
/// <see cref="Original"/> only for <see cref="CloudConflictChoice.KeepBoth"/>; carrying both makes the renamed
/// destination the single fact a caller has to read, instead of a rule each call site re-derives. A
/// <see cref="CloudConflictChoice.Skip"/> entry keeps its original so a batch paired by position stays
/// readable, and the choice is what tells the caller not to act on it.
/// </summary>
public sealed record CloudConflictPlacement(string Original, string Destination, CloudConflictChoice Choice);

/// <summary>
/// The conflict rules the cloud file browser runs when a destination name is already taken, kept out of the
/// window so a wrong answer is a failing test rather than a file that silently went missing. Both legs that
/// can hit a conflict resolve it here — an upload from disk and a 粘贴 between endpoints ask the same question
/// about different path shapes.
/// </summary>
public static class CloudConflictResolver
{
    /// <summary>
    /// The answer a saved policy gives for every conflict, or <see langword="null"/> when the policy says to
    /// ask. An unanswered question must not default to <see cref="CloudConflictChoice.Overwrite"/>: a setting
    /// that reads 每次询问 has to reach the user before anything is replaced.
    /// </summary>
    public static CloudConflictChoice? ChoiceFor(CloudUploadConflictPolicy policy) => policy switch
    {
        CloudUploadConflictPolicy.Overwrite => CloudConflictChoice.Overwrite,
        CloudUploadConflictPolicy.Skip => CloudConflictChoice.Skip,
        CloudUploadConflictPolicy.KeepBoth => CloudConflictChoice.KeepBoth,
        _ => null
    };

    /// <summary>
    /// Asks about <paramref name="conflicts"/> one at a time and stops asking once an answer covers the rest.
    /// A <see langword="null"/> from <paramref name="ask"/> is the user cancelling the batch, reported as
    /// <see langword="null"/> rather than as an empty dictionary — "nothing chosen" and "nothing left to
    /// choose" are different outcomes, and only the first tells the caller to touch nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, CloudConflictChoice>? AskFor(
        IReadOnlyList<string> conflicts,
        Func<string, int, CloudConflictAnswer?> ask)
    {
        var choices = new Dictionary<string, CloudConflictChoice>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < conflicts.Count; index++)
        {
            var path = conflicts[index];
            if (choices.ContainsKey(path)) continue;
            var answer = ask(path, conflicts.Count - index);
            if (answer is null) return null;
            if (answer.ApplyToAll)
                foreach (var rest in conflicts.Skip(index)) choices[rest] = answer.Choice;
            else choices[path] = answer.Choice;
        }
        return choices;
    }

    /// <summary>
    /// Turns answers into destinations, in the order the destinations were given and one per destination, so a
    /// caller can pair the result with its own rows by position instead of re-deriving the answer. A destination
    /// answered <see cref="CloudConflictChoice.Skip"/> carries that choice rather than disappearing, which is
    /// what keeps the pairing honest when a batch is only partly accepted. <paramref name="takenOf"/> reports
    /// the names one directory currently holds, and the set it returns grows as names are used: two files
    /// dropped onto the same remote name then become two files in the cloud rather than one, which is the whole
    /// promise of <see cref="CloudConflictChoice.KeepBoth"/>. Per directory rather than one shared set, because
    /// a free name in one directory is not free in another.
    /// </summary>
    public static IReadOnlyList<CloudConflictPlacement> Apply(
        IReadOnlyList<string> destinations,
        Func<string, ICollection<string>> takenOf,
        Func<string, CloudConflictChoice> choose)
    {
        var placements = new List<CloudConflictPlacement>(destinations.Count);
        foreach (var original in destinations)
        {
            var choice = choose(original);
            if (choice == CloudConflictChoice.Skip)
            {
                placements.Add(new CloudConflictPlacement(original, original, choice));
                continue;
            }
            var destination = original;
            if (choice == CloudConflictChoice.KeepBoth)
            {
                var taken = takenOf(CloudFileManagerService.Parent(original));
                var name = CloudFileNames.KeepBothName(CloudTransferTask.NameOf(original), taken);
                taken.Add(name);
                destination = CloudFileManagerService.Join(CloudFileManagerService.Parent(original), name);
            }
            placements.Add(new CloudConflictPlacement(original, destination, choice));
        }
        return placements;
    }
}
