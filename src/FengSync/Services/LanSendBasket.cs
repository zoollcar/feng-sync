using System.Collections.ObjectModel;
using System.ComponentModel;
using FengSync.Core.Configuration;
using FengSync.Lan;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// What this machine is willing to hand over, for as long as the window that filled it stays open.
/// </summary>
/// <remarks>
/// <para>
/// The basket <em>is</em> the permission boundary, and the window says so in its own wording rather than
/// leaving it as an implication: a peer holding a valid credential may list these rows and read these bytes,
/// and has no call that names a path. That is why the server side takes this as an <see cref="ILanSendSource"/>
/// — one method returning a snapshot — instead of a directory.
/// </para>
/// <para>
/// It is never written to disk. Not because a saved basket is dangerous, but because a row left over from last
/// week would still be readable next week, and nothing about that matches what closing the window is supposed
/// to do.
/// </para>
/// <para>
/// One collection, not two: the rows are what the screen draws and what the listener is allowed to serve, and a
/// second list holding the same items would let 清空 and a peer's <c>/basket</c> answer disagree. The wire form
/// is derived from the rows on demand rather than kept beside them.
/// </para>
/// </remarks>
public sealed class LanSendBasket : ILanSendSource, INotifyPropertyChanged
{
    private readonly object _gate = new();

    /// <summary>The rows, in the order the window shows them and the order a peer is told about.</summary>
    public ObservableCollection<LanSendRow> Rows { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Drives the empty-state hint, which is the only thing on screen that changes with the count.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate) return Rows.Count == 0;
        }
    }

    public IReadOnlyList<LanSendItem> Snapshot()
    {
        // Taken under the same gate the UI thread uses to mutate, because a listener thread copying the list
        // while a drop is in progress would otherwise hand the peer a half-added row or throw mid-enumeration.
        lock (_gate) return [.. Rows.Select(row => row.Item)];
    }

    /// <summary>
    /// Adds every path the person dropped or picked. A path that no longer exists, or that is already in the
    /// basket, is skipped rather than invented — dropping the same folder twice is one row, because two rows
    /// with one name would make the peer's file list disagree with itself.
    /// </summary>
    /// <returns>How many rows appeared, so the window can report the drop instead of assuming it worked.</returns>
    public int Add(IEnumerable<string?>? absolutePaths)
    {
        if (absolutePaths is null) return 0;
        var added = 0;
        lock (_gate)
        {
            foreach (var candidate in absolutePaths)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var full = System.IO.Path.GetFullPath(candidate);
                if (!System.IO.File.Exists(full) && !System.IO.Directory.Exists(full)) continue;
                if (Rows.Any(row => string.Equals(row.Item.AbsolutePath, full, StringComparison.OrdinalIgnoreCase))) continue;
                var item = LanSendItem.Of(full);
                Rows.Add(new LanSendRow(item, item.Name, Describe(item)));
                added++;
            }
        }

        if (added > 0) RaiseIsEmpty();
        return added;
    }

    /// <summary>Takes one row out. The bytes it pointed at are untouched — this is a list, not a move.</summary>
    public bool Remove(LanSendRow? row)
    {
        if (row is null) return false;
        bool removed;
        lock (_gate) removed = Rows.Remove(row);
        if (removed) RaiseIsEmpty();
        return removed;
    }

    /// <summary>Empties the basket. Called on the way out as well, so a reused window never shows rows from its own earlier visit.</summary>
    public void Clear()
    {
        lock (_gate) Rows.Clear();
        RaiseIsEmpty();
    }

    /// <summary>
    /// A folder's row says 文件夹 rather than 0 B. The size of a subtree is a walk nobody asked for at the moment
    /// a row is added — the same reason the cloud 属性 panel leaves a folder's size 未统计 until a button is
    /// pressed to measure it.
    /// </summary>
    private static string Describe(LanSendItem item) => item.IsDirectory
        ? Tr.F("Lan.BasketFolder")
        : Tr.Format("Lan.BasketFile", ByteSize.Format(item.Bytes));

    private void RaiseIsEmpty() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
}

/// <summary>One basket row as the screen draws it, with the item the listener may serve.</summary>
public sealed record LanSendRow(LanSendItem Item, string Name, string Detail);
