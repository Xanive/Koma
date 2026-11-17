using System.Windows.Controls;
using System.Windows;

namespace Koma;

public partial class Controls : UserControl
{
    public Controls() => InitializeComponent();
    private void ResizeControls(object sender, SizeChangedEventArgs e)
    {
        if (Speed is null) return;
        bool compact = ActualWidth < 900;
        Speed.Visibility = Chapters.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        More.Visibility = Visibility.Visible;
    }
}
