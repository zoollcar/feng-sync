namespace FengSync.ViewModels;

public sealed record FeatureOptionViewModel(string Label, bool IsAvailable, string? UnavailableReason = null);
