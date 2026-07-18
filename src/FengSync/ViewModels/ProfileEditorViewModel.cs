using System.ComponentModel;
using System.Runtime.CompilerServices;
using FengSync.Core;
using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;

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
    public IReadOnlyList<ProfileSectionViewModel> Sections { get; } = [new("常规", "general"), new("比较", "comparison"), new("过滤器", "filter"), new("同步", "sync"), new("版本管理", "versioning"), new("性能与可靠性", "performance")];
    public void Update(Func<SyncProfile, SyncProfile> update) => Profile = update(Profile);
    public SyncProfile SaveAsNew() => Profile with { Id = Guid.NewGuid().ToString("N") };
    private void RefreshCompatibility() { Compatibility = new FeatureCapabilityService().Evaluate(Profile); OnPropertyChanged(nameof(Compatibility)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
