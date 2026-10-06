using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FengSync.Localization;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// Draws the panel <see cref="CloudBrowserPreview"/> decided. It owns no rule: which row may be painted, what an
/// unpaintable one is called, and what the ceiling was are all answered before this is reached, which is what keeps
/// the preview testable — the same way <see cref="CloudDetailsPrompt"/> is. Built in code like the other prompts
/// because "one picture, one block of text, or one sentence" is not a layout worth a XAML window.
/// </summary>
internal static class CloudPreviewPrompt
{
    /// <summary>The width the panel asks for, and the reason <see cref="CloudBrowserPreview.PreviewEdgePixels"/> is that number.</summary>
    public const double WindowWidth = 1000;

    public static void Show(Window owner, string title, CloudFileEntry entry, CloudPreviewPanel panel)
    {
        var window = TextPrompt.NewWindow(title, owner, WindowWidth);
        var body = new StackPanel { Margin = new Thickness(20) };

        AutomationProperties.SetAutomationId(body, "CloudPreview");
        var caption = new TextBlock
        {
            Style = TextPrompt.Resource<Style>("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        AutomationProperties.SetAutomationId(caption, "CloudPreviewCaption");
        body.Children.Add(caption);

        switch (panel.Outcome)
        {
            case CloudPreviewOutcome.Picture when panel.Image is { } image:
                var picture = new Image
                {
                    Source = image,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 620,
                    MaxWidth = WindowWidth - 60,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                AutomationProperties.SetAutomationId(picture, "CloudPreviewImage");
                AutomationProperties.SetName(picture, entry.Name);
                body.Children.Add(picture);
                caption.Text = Tr.Format("Preview.CaptionPicture", entry.Name, CloudItemDetails.HumanSize(entry.Size), CloudBrowserPreview.PreviewEdgePixels);
                break;
            case CloudPreviewOutcome.Text when panel.Content is { } text:
                var scroll = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                var content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, Segoe UI"),
                    Padding = new Thickness(10, 8, 10, 8)
                };
                AutomationProperties.SetAutomationId(content, "CloudPreviewText");
                AutomationProperties.SetName(content, entry.Name);
                scroll.Content = content;
                body.Children.Add(scroll);
                caption.Text = Tr.Format("Preview.CaptionText", entry.Name, CloudItemDetails.HumanSize(entry.Size), panel.TextEncoding);
                break;
            default:
                // Nothing painted: the sentence is the whole answer, and it is written to name the reason rather
                // than to apologise, so the user knows what to do next (list the folder, download the file).
                var refusal = new TextBlock
                {
                    Text = panel.Message ?? CloudBrowserPreview.ReasonFor(panel.Outcome, entry),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                AutomationProperties.SetAutomationId(refusal, "CloudPreviewMessage");
                AutomationProperties.SetName(refusal, entry.Name);
                body.Children.Add(refusal);
                caption.Text = Tr.Format("Preview.Unavailable", entry.Name);
                break;
        }

        var close = new Button
        {
            Content = Tr.F("Preview.Close"),
            IsDefault = true,
            IsCancel = true,
            Style = TextPrompt.Resource<Style>("TertiaryButtonStyle"),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetAutomationId(close, "CloudPreviewClose");
        body.Children.Add(close);

        window.Content = body;
        window.ShowDialog();
    }
}
