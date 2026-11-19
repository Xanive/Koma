using System.Windows;
using System.Windows.Controls;

namespace Koma;

internal static class UiLocalization
{
    public static void Apply(string language)
    {
        UiText.Select(language);
        foreach (string key in UiText.Catalog("en").Keys)
            Application.Current.Resources["Koma.Text." + key] = UiText.Translate(key);
    }
}

public partial class MainWindow
{
    private void RefreshLanguage()
    {
        string? season = displayedSeason?.Name;
        string? episode = displayedSeason?.Episodes.FirstOrDefault()?.Key;
        UiLocalization.Apply(Settings.UiLanguage);
        transportMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        displayingFolders = true;
        Folders.Items.Refresh();
        displayingFolders = false;
        RenderFolder(season, episode);
        RenderContinue();
        Detail.Content = UiText.Translate(detailZoom ? "Fit" : "Detail");
        SetStatus(originalStatus);
        UpdateTransport();
        UpdateShell();
        RenderTrackingFilters(); RenderTracking();
        RefreshDownloadsLanguage();
    }
}
