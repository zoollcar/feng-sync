using System.Windows;
using FengSync.Core;
using FengSync.Localization;

namespace FengSync.Views;

/// <summary>Explicit confirmation for destructive/overwrite plans; threshold overrides require typing the profile name.</summary>
public partial class SyncConfirmationWindow : Window
{
    private readonly string _profileName;
    private readonly bool _requiresProfileName;
    public SyncConfirmationWindow(SyncRiskSummary summary, SafetyValidationResult safety, string profileName, long requiredBytes = 0)
    {
        InitializeComponent(); _profileName = profileName; _requiresProfileName = SyncConfirmationPolicy.CanOverrideWithProfileName(safety);
        TitleText.Text = _requiresProfileName ? Tr.F("Sync.ThresholdExceeded") : Tr.F("Sync.HasOverwriteOrDelete");
        CopyText.Text = Tr.Format("Sync.Copies", summary.Copies); OverwriteText.Text = Tr.Format("Sync.Overwrites", summary.Overwrites);
        DeleteText.Text = Tr.Format("Sync.Deletes", summary.Deletes); TransferText.Text = Tr.Format("Sync.Transfer", FormatBytes(summary.TransferBytes));
        SpaceText.Text = requiredBytes > 0 ? Tr.Format("Sync.SpaceRequired", FormatBytes(requiredBytes)) : Tr.F("Sync.SpaceVerified");
        IssuesText.Text = string.Join(Environment.NewLine, safety.Issues.Select(x => x.Message));
        IssuesCallout.Visibility = safety.Issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_requiresProfileName) { ProfileConfirmationPanel.Visibility = Visibility.Visible; ProfileConfirmationPrompt.Text = Tr.Format("Sync.TypeProfileName", profileName); }
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_requiresProfileName && !string.Equals(ProfileNameInput.Text.Trim(), _profileName, StringComparison.Ordinal))
        { MessageBox.Show(Tr.F("Sync.NameMismatch"), Tr.F("Sync.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
    private static string FormatBytes(long value) => value < 1024 ? $"{value:N0} B" : value < 1024 * 1024 ? $"{value / 1024d:N1} KB" : $"{value / 1024d / 1024:N1} MB";
}
