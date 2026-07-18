using System.Windows;
using FengSync.Core;
using FengSync.Views;

namespace FengSync.Services;

public sealed class ProfileDialogService
{
    public SyncProfile? Edit(Window owner, SyncProfile profile)
    {
        var window = new ProfileEditorWindow(profile) { Owner = owner };
        return window.ShowDialog() == true ? window.SavedProfile : null;
    }
}
