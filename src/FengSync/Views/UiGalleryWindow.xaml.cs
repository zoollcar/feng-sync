using System.Windows;

namespace FengSync.Views;

/// <summary>
/// Development-only preview of the shared Feng Sync visual language.
/// It is deliberately not linked from production navigation.
/// </summary>
public partial class UiGalleryWindow : Window
{
    public UiGalleryWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
