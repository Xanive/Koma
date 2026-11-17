using System.Windows;

namespace Koma;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (MainWindow is not null) return;
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}
