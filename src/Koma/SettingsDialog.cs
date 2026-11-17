using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Koma;

public partial class MainWindow
{
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new Window {
            Owner = this, Title = "Playback settings", Width = 465, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Background = Background, Foreground = Foreground, FontSize = 14
        };
        var content = new StackPanel { Margin = new Thickness(20) };
        var seek = Field("Seek step (seconds)", Settings.SeekStep.ToString());
        var volume = Field("Volume step (%)", Settings.VolumeStep.ToString());
        var delay = Field("Fullscreen hide delay (seconds)", Settings.FullscreenDelay.ToString());
        var audio = Field("Preferred audio (jpn,eng…)", Settings.AudioLanguage);
        var subs = Field("Preferred subtitles (eng,deu…)", Settings.SubtitleLanguage);
        var resume = new CheckBox { Content = "Resume episodes", IsChecked = Settings.Resume, Foreground = Foreground };
        var autoplay = new CheckBox { Content = "Play next automatically", IsChecked = Settings.Autoplay, Foreground = Foreground,
            Margin = new Thickness(0, 8, 0, 0) };
        content.Children.Add(resume);
        content.Children.Add(autoplay);
        var help = new TextBlock { Text = "Language choices apply when opening the next episode.", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 10), Foreground = (Brush)FindResource("MutedBrush") };
        content.Children.Add(help);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Save", IsDefault = true };
        actions.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        actions.Children.Add(save);
        content.Children.Add(actions);
        content.Children.Add(new TextBlock
        {
            Text = "Anime4K upscaling by bloc97 and contributors",
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush")
        });
        save.Click += (_, _) =>
        {
            if (!int.TryParse(seek.Text, out int seconds) || seconds is < 1 or > 120 ||
                !int.TryParse(volume.Text, out int percent) || percent is < 1 or > 25 ||
                !int.TryParse(delay.Text, out int timeout) || timeout is < 1 or > 15)
            {
                help.Text = "Use 1–120 seconds for seek, 1–25% for volume, and 1–15 seconds for fullscreen.";
                return;
            }
            string audioCodes = Codes(audio.Text), subtitleCodes = Codes(subs.Text);
            if (!Valid(audioCodes) || !Valid(subtitleCodes))
            {
                help.Text = "Enter comma-separated language codes such as jpn,eng, or leave the field blank.";
                return;
            }
            Settings.SeekStep = seconds;
            Settings.VolumeStep = percent;
            Settings.FullscreenDelay = timeout;
            Settings.AudioLanguage = audioCodes;
            Settings.SubtitleLanguage = subtitleCodes;
            Settings.Resume = resume.IsChecked == true;
            Settings.Autoplay = autoplay.IsChecked == true;
            dialog.DialogResult = true;
        };
        dialog.Content = content;
        if (dialog.ShowDialog() != true) return;
        SaveSettings();
        RenderEpisodes();
        RenderContinue();

        TextBox Field(string name, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            var box = new TextBox { Text = value, MinWidth = 105, Padding = new Thickness(4, 3, 4, 3),
                Background = (Brush)FindResource("SurfaceBrush"), Foreground = Foreground };
            System.Windows.Automation.AutomationProperties.SetName(box, name);
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(row);
            return box;
        }

        static string Codes(string value) => value.Replace(" ", "").ToLowerInvariant();
        static bool Valid(string value) => value.Length < 80 &&
            (value.Length == 0 || Regex.IsMatch(value, @"\A[a-z]{2,3}(-[a-z0-9]{2,8})?(,[a-z]{2,3}(-[a-z0-9]{2,8})?)*\z"));
    }
}
