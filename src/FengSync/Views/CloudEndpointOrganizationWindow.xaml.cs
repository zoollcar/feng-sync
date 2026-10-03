using System.Windows;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// Files one endpoint under a group and gives it tags. A dialog rather than two chained text prompts: the
/// group list is worth seeing whole while deciding, and the tags are worth being able to correct without
/// re-answering the group question.
/// </summary>
public partial class CloudEndpointOrganizationWindow : Window
{
    public CloudEndpointOrganizationWindow(string endpointName, string? group, IReadOnlyList<string>? tags,
        IEnumerable<string> existingGroups)
    {
        InitializeComponent();
        HeaderText.Text = $"“{endpointName}”的分组与标签";
        SubHeaderText.Text = "分组决定它在列表里的位置，标签用来在搜索框里找到它。";
        GroupTextBox.Text = group ?? "";
        TagsBox.Text = CloudEndpointOrganization.FormatTags(tags);
        var known = existingGroups.Select(CloudEndpointOrganization.NormalizeGroup)
            .OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCulture).ToList();
        GroupHint.Text = known.Count == 0
            ? "还没有其他分组。填一个名称即可新建分组；留空表示移出分组。"
            : "已有分组：" + string.Join("、", known) + "。留空表示移出分组。";
    }

    /// <summary>
    /// What the user entered, normalised. Computed from the boxes rather than cached on OK, so a caller that
    /// reads them after a modal return cannot see a stale value.
    /// </summary>
    public string? Group => CloudEndpointOrganization.NormalizeGroup(GroupTextBox.Text);

    /// <summary>The parsed tags, or null when the endpoint should have none.</summary>
    public IReadOnlyList<string>? Tags => CloudEndpointOrganization.ParseTags(TagsBox.Text);

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
