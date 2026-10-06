using System.ComponentModel;
using System.Runtime.CompilerServices;
using FengSync.Core;
using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;
using FengSync.Localization;

namespace FengSync.ViewModels;

/// <summary>A detached edit buffer. The caller receives a profile only after validation and Save.</summary>
public sealed class ProfileEditorViewModel : INotifyPropertyChanged
{
    private SyncProfile _profile;
    private readonly SyncProfile _original;
    public ProfileEditorViewModel(SyncProfile profile) { _original = profile; _profile = profile with { Settings = profile.Settings }; RefreshCompatibility(); }
    public SyncProfile Profile { get => _profile; private set { _profile = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDirty)); RefreshCompatibility(); } }
    public bool IsDirty => !Equals(Profile, _original);
    public ProfileCompatibilityResult Compatibility { get; private set; } = new([], []);
    // The second element of each pair is the section's stable id — never translated; the first is only the label.
    public IReadOnlyList<ProfileSectionViewModel> Sections { get; } =
    [
        new(Tr.F("ProfileEditor.SectionGeneral"), "general"),
        new(Tr.F("ProfileEditor.SectionComparison"), "comparison"),
        new(Tr.F("ProfileEditor.SectionFilter"), "filter"),
        new(Tr.F("ProfileEditor.SectionSync"), "sync"),
        new(Tr.F("ProfileEditor.SectionVersioning"), "versioning"),
        new(Tr.F("ProfileEditor.SectionPerformance"), "performance")
    ];
    public void Update(Func<SyncProfile, SyncProfile> update) => Profile = update(Profile);
    public SyncProfile SaveAsNew() => Profile with { Id = Guid.NewGuid().ToString("N") };
    private void RefreshCompatibility() { Compatibility = new FeatureCapabilityService().Evaluate(Profile); OnPropertyChanged(nameof(Compatibility)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
