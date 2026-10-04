using System.Windows.Input;

namespace FengSync.Services;

/// <summary>
/// A keystroke the file browser owns. Each value names the command its toolbar button already runs, so the
/// keyboard is a second route to an existing behaviour rather than a behaviour of its own.
/// </summary>
public enum CloudBrowserShortcut
{
    Refresh,
    Recycle,
    DeleteForever,
    Rename,

    /// <summary>Every row currently on screen, which is the filtered and ordered view rather than the directory.</summary>
    SelectAll,
    Copy,
    Cut,
    Paste
}

/// <summary>
/// The browser's keystroke table as one pure function, kept away from the window so the two rules that decide
/// what a key does can be stated and tested on their own: which keystrokes mean anything here at all, and that a
/// keystroke delivered from a text field belongs to that field. The window supplies the facts and dispatches the
/// answer; nothing in the table acts.
/// </summary>
public static class CloudBrowserShortcuts
{
    /// <summary>
    /// The command <paramref name="key"/> names, or <see langword="null"/> when it names none.
    /// <paramref name="typing"/> is true when the keystroke reached the browser from a text field. F5 still
    /// refreshes then, because a browser has always done that and a refresh cannot collide with what the user is
    /// writing; the rest are left to the field, whose Del, F2 and Ctrl+A/C/X/V edit the text. A Ctrl+A that
    /// selected every row while the user meant the address bar is the kind of surprise that makes a shortcut
    /// unsafe to offer at all, so the gate is the shortcut's main risk and its first test.
    /// </summary>
    public static CloudBrowserShortcut? Resolve(Key key, bool ctrl, bool shift, bool typing)
    {
        if (key == Key.F5) return CloudBrowserShortcut.Refresh;
        if (typing) return null;

        // Shift and Ctrl are the two spellings file managers use for "skip the undoable step", so both answer
        // 删除 while a plain Del answers 移到回收站.
        if (key == Key.Delete) return ctrl || shift ? CloudBrowserShortcut.DeleteForever : CloudBrowserShortcut.Recycle;
        if (key == Key.F2) return CloudBrowserShortcut.Rename;
        // Bare letters stay bare: the list types-to-selects, and swallowing "a" would break it.
        if (!ctrl) return null;

        return key switch
        {
            Key.A => CloudBrowserShortcut.SelectAll,
            Key.C => CloudBrowserShortcut.Copy,
            Key.X => CloudBrowserShortcut.Cut,
            Key.V => CloudBrowserShortcut.Paste,
            _ => null
        };
    }
}
