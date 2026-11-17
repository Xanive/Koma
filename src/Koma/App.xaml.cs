using System.Windows;

namespace Koma;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(172, 141, 245),
            Wpf.Ui.Appearance.ApplicationTheme.Dark);
        if (MainWindow is not null) return;
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}
