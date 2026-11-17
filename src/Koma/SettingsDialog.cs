using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Koma;

public partial class MainWindow
{
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new Wpf.Ui.Controls.FluentWindow {
            Owner = this, Title = UiText.Translate("Playback settings"), Width = 465, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 60), ExtendsContentIntoTitleBar = true,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Background = Background, Foreground = Foreground, FontSize = 14
        };
        var content = new StackPanel { Margin = new Thickness(20) };
        Heading(UiText.Translate("Interface language"));
        var language = new ComboBox { ItemsSource = new[] { new LanguageChoice("en", "English"),
            new LanguageChoice("de", "Deutsch"), new LanguageChoice("fr", "Français") },
            DisplayMemberPath = "Name", SelectedValuePath = "Code", SelectedValue = Settings.UiLanguage,
            Margin = new Thickness(0, 0, 0, 16), MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(language, UiText.Translate("Interface language"));
        content.Children.Add(language);
        Heading(UiText.Translate("Playback"));
        var seek = Field(UiText.Translate("Seek step (seconds)"), Settings.SeekStep.ToString());
        var volume = Field(UiText.Translate("Volume step (%)"), Settings.VolumeStep.ToString());
        var delay = Field(UiText.Translate("Fullscreen hide delay (seconds)"), Settings.FullscreenDelay.ToString());
        var resume = new CheckBox { Content = UiText.Translate("Resume episodes"), IsChecked = Settings.Resume, Foreground = Foreground };
        var autoplay = new CheckBox { Content = UiText.Translate("Play next automatically"), IsChecked = Settings.Autoplay, Foreground = Foreground,
            Margin = new Thickness(0, 8, 0, 0) };
        content.Children.Add(resume);
        content.Children.Add(autoplay);
        Heading(UiText.Translate("Languages"));
        var audio = Field(UiText.Translate("Preferred audio (jpn,eng…)"), Settings.AudioLanguage);
        var subs = Field(UiText.Translate("Preferred subtitles (eng,deu…)"), Settings.SubtitleLanguage);
        var help = new TextBlock { Text = UiText.Translate("Language choices apply when opening the next episode."), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 10), Foreground = (Brush)FindResource("MutedBrush") };
        content.Children.Add(help);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = UiText.Translate("Save"), IsDefault = true };
        actions.Children.Add(new Button { Content = UiText.Translate("Cancel"), IsCancel = true });
        actions.Children.Add(save);
        content.Children.Add(actions);
        Heading(UiText.Translate("Credits"));
        content.Children.Add(new TextBlock
        {
            Text = UiText.Translate("Anime4K upscaling by bloc97 and contributors"),
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
                help.Text = UiText.Translate("Use 1–120 seconds for seek, 1–25% for volume, and 1–15 seconds for fullscreen.");
                return;
            }
            string audioCodes = Codes(audio.Text), subtitleCodes = Codes(subs.Text);
            if (!Valid(audioCodes) || !Valid(subtitleCodes))
            {
                help.Text = UiText.Translate("Enter comma-separated language codes such as jpn,eng, or leave the field blank.");
                return;
            }
            Settings.SeekStep = seconds;
            Settings.VolumeStep = percent;
            Settings.FullscreenDelay = timeout;
            Settings.AudioLanguage = audioCodes;
            Settings.SubtitleLanguage = subtitleCodes;
            Settings.Resume = resume.IsChecked == true;
            Settings.Autoplay = autoplay.IsChecked == true;
            Settings.UiLanguage = language.SelectedValue as string ?? "en";
            dialog.DialogResult = true;
        };
        var root = new DockPanel { Background = (Brush)FindResource("BackgroundBrush") };
        var title = new Wpf.Ui.Controls.TitleBar { Title = UiText.Translate("Settings"), Height = 44, ShowMinimize = false, ShowMaximize = false,
            Background = (Brush)FindResource("BackgroundBrush") };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root;
        if (dialog.ShowDialog() != true) return;
        SaveSettings();
        RefreshLanguage();

        void Heading(string title) => content.Children.Add(new TextBlock { Text = title, FontSize = 18,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 16) });

        TextBox Field(string name, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            var box = new TextBox { Text = value, MinWidth = 105, Padding = new Thickness(4, 3, 4, 3),
                Background = (Brush)FindResource("SurfaceBrush"), Foreground = Foreground };
            System.Windows.Automation.AutomationProperties.SetName(box, name);
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(row);
            return box;
        }

        static string Codes(string value) => value.Replace(" ", "").ToLowerInvariant();
        static bool Valid(string value) => value.Length < 80 &&
            (value.Length == 0 || Regex.IsMatch(value, @"\A[a-z]{2,3}(-[a-z0-9]{2,8})?(,[a-z]{2,3}(-[a-z0-9]{2,8})?)*\z"));
    }

    private record LanguageChoice(string Code, string Name);
}
