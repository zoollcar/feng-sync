using System.Diagnostics;
using System.Windows;

namespace FengSync;
public partial class ProgressWindow : Window
{
    private readonly int _total; private int _done; private readonly Stopwatch _clock = Stopwatch.StartNew();
    public ProgressWindow(int total, bool autoClose) { InitializeComponent(); _total = Math.Max(1, total); AutoClose.IsChecked = autoClose; Counter.Text = $"0 / {total}"; FileProgress.Maximum = _total; }
    public void Report(string path) { _done++; FileProgress.Value = _done; BytesGraph.Value = _done * 100.0 / _total; SpeedGraph.Value = Math.Min(100, _done * 100.0 / Math.Max(1, _clock.Elapsed.TotalSeconds)); Counter.Text = $"{_done} / {_total}"; CurrentFile.Text = path; BytesText.Text = $"已完成 {_done} 个文件"; SpeedText.Text = $"{_done / Math.Max(.1, _clock.Elapsed.TotalSeconds):N1} 文件/秒"; }
    public void Complete(bool success, string text) { StateIcon.Text = success ? "✓" : "!"; StateIcon.Foreground = success ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.Firebrick; StateTitle.Text = success ? "已成功完成" : "同步未完成"; StateDescription.Text = text; CloseButton.IsEnabled = true; if (success && AutoClose.IsChecked == true) Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
