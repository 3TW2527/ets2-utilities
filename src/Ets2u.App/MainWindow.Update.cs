using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Ets2u.Core;
using Ets2u.Core.Update;

namespace Ets2u.App;

/// <summary>Updating the app from its GitHub releases: the bar under the title and the controls on Start here.</summary>
public partial class MainWindow
{
    UpdateInfo? _update;
    bool _updating;

    void InitUpdates()
    {
        AppVersionText.Text = "Version " + Updater.Text(Updater.Current);
        CheckUpdatesBox.IsChecked = _app.Settings.CheckForUpdates;
        if (_app.Settings.CheckForUpdates) _ = CheckForUpdate(quiet: true);
    }

    /// <summary>A dev build (dotnet run, bin\Debug) has ets2u.dll beside it: it can't be swapped for a release's single exe.</summary>
    static bool DevBuild => File.Exists(Path.Combine(AppContext.BaseDirectory, "ets2u.dll"));

    async Task CheckForUpdate(bool quiet)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            using var http = Updater.CreateClient();
            _update = await Updater.CheckAsync(http);
            if (_update is null)
            {
                if (!quiet) _app.Status($"You have the latest version ({Updater.Text(Updater.Current)}).");
                UpdateBar.Visibility = Visibility.Collapsed;
                return;
            }
            ShowUpdate($"ETS2 Utilities {_update.VersionText} is out (you have {Updater.Text(Updater.Current)}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            AppDirs.Log("Checking for updates: " + ex.Message);
            if (!quiet) _app.Status("Couldn't reach GitHub to check for updates: " + ex.Message);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    void ShowUpdate(string text)
    {
        UpdateText.Text = text;
        UpdateBar.Visibility = Visibility.Visible;
        UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = !_updating;
    }

    async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update || _updating) return;
        var target = AppContext.BaseDirectory;
        if (DevBuild || !Updater.CanWrite(target))
        {
            MessageBox.Show(this, DevBuild
                    ? "This is a development build, so it can't update itself. The release page opens instead."
                    : $"The app can't change its own folder ({target}). The release page opens instead: download the zip and unzip it over this folder.",
                "ETS2 Utilities", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenUpdatePage();
            return;
        }

        _updating = true;
        ShowUpdate($"Downloading {update.VersionText}…");
        try
        {
            using var http = Updater.CreateClient();
            var progress = new Progress<double>(p => UpdateText.Text = $"Downloading {update.VersionText}… {p:P0}");
            var staged = await Updater.DownloadAsync(http, update, progress);
            UpdateText.Text = $"Installing {update.VersionText}: the app closes and opens again in a few seconds.";
            Updater.Start(staged, target);
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppDirs.Log("Update: " + ex);
            _updating = false;
            ShowUpdate($"The update to {update.VersionText} didn't work: {ex.Message}");
            MessageBox.Show(this, "The update didn't work:\n\n" + ex.Message + "\n\nThe app is unchanged.",
                "ETS2 Utilities", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void UpdateNotes_Click(object sender, RoutedEventArgs e) => OpenUpdatePage();

    void OpenUpdatePage()
    {
        var url = _update?.Page ?? $"https://github.com/{Updater.Repository}/releases/latest";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { _app.Status("Couldn't open the browser: " + ex.Message); }
    }

    void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    void CheckUpdates_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdate(quiet: false);

    void CheckUpdatesBox_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.CheckForUpdates = CheckUpdatesBox.IsChecked == true;
        try { _app.Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppDirs.Log("settings.json: " + ex.Message); }
    }
}
