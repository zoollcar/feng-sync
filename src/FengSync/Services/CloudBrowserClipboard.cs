namespace FengSync.Services;

/// <summary>
/// What the file browser's 复制 / 剪切 holds. The two differ in exactly one thing: a cut removes the source
/// once the paste has worked, and that removal is the only destructive step in the paste.
/// </summary>
public enum CloudClipboardMode { Copy, Cut }

/// <summary>
/// One row waiting to be pasted, kept together with the endpoint it came from. The user can navigate to a
/// different endpoint before pasting — that is the point of a cloud-to-cloud copy — and a listing row's path
/// is meaningless without the remote it was listed from.
/// </summary>
public sealed record CloudClipboardItem(CloudEndpointAccount Source, CloudFileEntry Entry);

/// <summary>
/// Where one clipboard row lands in the directory being pasted into. The two path shapes are both here on
/// purpose: <see cref="DestinationPath"/> is relative to the rclone remote, which is what
/// <see cref="CloudFileManagerService"/> addresses, while the relative pair is what an endpoint URI addresses
/// and therefore what the transfer queue needs. Computing them at the call site instead would put the
/// root-prefix rule back in the window, where a paste across two endpoints with two different roots is
/// exactly the place it is easiest to get wrong.
/// </summary>
public sealed record CloudPasteTarget(
    CloudClipboardItem Item,
    string DestinationPath,
    string SourceRelativePath,
    string DestinationRelativePath,
    bool SameEndpoint,
    bool OverwritesExisting);

/// <summary>
/// What a paste would do: the rows that can land, and the readable reasons for the rows that cannot.
/// </summary>
/// <param name="Skipped">One line per row the paste will not copy, in the order the rows were clipboarded.</param>
public sealed record CloudPastePlan(IReadOnlyList<CloudPasteTarget> Targets, IReadOnlyList<string> Skipped)
{
    /// <summary>How many targets would replace something that is already there.</summary>
    public int Conflicts => Targets.Count(target => target.OverwritesExisting);
}

/// <summary>
/// The paste rules of the cloud file browser, kept free of WPF and of rclone so they can be tested directly:
/// where a clipboard row lands, which rows cannot be pasted, and which ones would overwrite. The queue and the
/// move call are deliberately not here — this type decides <em>what a paste means</em>, not how it runs.
/// </summary>
public static class CloudPastePlanner
{
    /// <summary>
    /// Plans a paste of <paramref name="items"/> into <paramref name="destinationDirectory"/> of
    /// <paramref name="destination"/>, where <paramref name="alreadyThere"/> is that directory's current
    /// listing. The listing is a parameter rather than fetched here so the plan stays a pure function of what
    /// the browser already knows — and so a conflict can be detected before anything is queued.
    /// </summary>
    public static CloudPastePlan Build(
        IReadOnlyList<CloudClipboardItem> items,
        CloudEndpointAccount destination,
        string destinationDirectory,
        IReadOnlyList<CloudFileEntry> alreadyThere)
    {
        var targets = new List<CloudPasteTarget>();
        var skipped = new List<string>();
        var folders = 0;
        foreach (var item in items)
        {
            // The transfer queue's unit is one object, and a directory copy is a different operation from a
            // file copy; the browser's answer for a folder has always been "enter it first" (see 下载).
            if (item.Entry.IsDirectory) { folders++; continue; }
            var sameEndpoint = IsSameEndpoint(item.Source.Name, destination.Name);
            var path = CloudFileManagerService.Join(destinationDirectory, item.Entry.Name);
            if (sameEndpoint && IsSamePath(item.Entry.Path, path))
            {
                // Copying a file onto itself is not a no-op worth queuing: rclone would report it as a
                // successful copy while the user watched a transfer that could not change anything.
                skipped.Add($"“{item.Entry.Name}”已经在目标目录里。");
                continue;
            }
            targets.Add(new CloudPasteTarget(
                item,
                path,
                CloudFileManagerService.RelativeToEndpoint(item.Source, item.Entry.Path),
                CloudFileManagerService.RelativeToEndpoint(destination, path),
                sameEndpoint,
                // Any same-name entry blocks the name, not only a file: on a backend without real
                // directories a folder marker occupies the very key the paste would write.
                OverwritesExisting: alreadyThere.Any(entry => entry.Name.Equals(item.Entry.Name, StringComparison.OrdinalIgnoreCase))));
        }
        if (folders > 0) skipped.Add($"{folders} 个文件夹没有被复制：粘贴的单位是文件，请先进入文件夹再选择其中的文件。");
        return new CloudPastePlan(targets, skipped);
    }

    private static bool IsSameEndpoint(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool IsSamePath(string left, string right) =>
        left.Trim('/').Equals(right.Trim('/'), StringComparison.OrdinalIgnoreCase);
}
