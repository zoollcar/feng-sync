using System.Windows;
using FengSync.Localization;
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
        HeaderText.Text = Tr.Format("Organize.Header", endpointName);
        SubHeaderText.Text = Tr.F("Organize.SubHeader");
        GroupTextBox.Text = group ?? "";
        TagsBox.Text = CloudEndpointOrganization.FormatTags(tags);
        var known = existingGroups.Select(CloudEndpointOrganization.NormalizeGroup)
            .OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCulture).ToList();
        GroupHint.Text = known.Count == 0
            ? Tr.F("Organize.NoGroupsYet")
            : Tr.Format("Organize.KnownGroups", string.Join(Tr.F("Organize.GroupSeparator"), known));
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
