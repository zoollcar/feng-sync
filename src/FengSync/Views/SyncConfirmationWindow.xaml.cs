using System.Windows;
using FengSync.Core;

namespace FengSync.Views;

/// <summary>Explicit confirmation for destructive/overwrite plans; threshold overrides require typing the profile name.</summary>
public partial class SyncConfirmationWindow : Window
{
    private readonly string _profileName;
    private readonly bool _requiresProfileName;
    public SyncConfirmationWindow(SyncRiskSummary summary, SafetyValidationResult safety, string profileName, long requiredBytes = 0)
    {
        InitializeComponent(); _profileName = profileName; _requiresProfileName = SyncConfirmationPolicy.CanOverrideWithProfileName(safety);
        TitleText.Text = _requiresProfileName ? "删除阈值超限：需要明确确认" : "同步包含覆盖或删除操作";
        CopyText.Text = $"复制：{summary.Copies} 项"; OverwriteText.Text = $"覆盖：{summary.Overwrites} 项";
        DeleteText.Text = $"删除：{summary.Deletes} 项"; TransferText.Text = $"传输：{FormatBytes(summary.TransferBytes)}";
        SpaceText.Text = requiredBytes > 0 ? $"估算目标空间需求：{FormatBytes(requiredBytes)}" : "空间检查：已按本地目标可用空间验证。";
        IssuesText.Text = string.Join(Environment.NewLine, safety.Issues.Select(x => x.Message));
        if (_requiresProfileName) { ProfileConfirmationPanel.Visibility = Visibility.Visible; ProfileConfirmationPrompt.Text = $"要一次性放行删除阈值，请输入 Profile 名称“{profileName}”。"; }
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_requiresProfileName && !string.Equals(ProfileNameInput.Text.Trim(), _profileName, StringComparison.Ordinal))
        { MessageBox.Show("输入的 Profile 名称不匹配，未执行同步。", "确认同步", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
    private static string FormatBytes(long value) => value < 1024 ? $"{value:N0} B" : value < 1024 * 1024 ? $"{value / 1024d:N1} KB" : $"{value / 1024d / 1024:N1} MB";
}
