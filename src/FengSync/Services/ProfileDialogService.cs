using System.Windows;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Views;

namespace FengSync.Services;

public sealed class ProfileDialogService
{
    /// <summary><paramref name="application"/> is passed so the editor can show the program defaults an
    /// inheriting profile would actually use, rather than a hard-coded guess.</summary>
    public SyncProfile? Edit(Window owner, SyncProfile profile, ApplicationSettings? application = null)
    {
        var window = new ProfileEditorWindow(profile, application) { Owner = owner };
        return window.ShowDialog() == true ? window.SavedProfile : null;
    }
}
