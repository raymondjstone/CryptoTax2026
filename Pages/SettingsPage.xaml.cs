using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using CryptoTax2026.Models;
using CryptoTax2026.Services;

namespace CryptoTax2026.Pages;

public sealed partial class SettingsPage : Page
{
    private MainWindow? _mainWindow;
    private CancellationTokenSource? _cts;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainWindow mw)
        {
            _mainWindow = mw;
            LoadSettings();
            UpdateLedgerStatus();
            UpdateFxStatus();
            UpdateFreshnessIndicator();
            LoadAuditLog();
            LoadThemeSelector();
        }
    }

    private void LoadSettings()
    {
        if (_mainWindow == null) return;

        ApiKeyBox.Text = _mainWindow.Settings.KrakenApiKey;
        ApiSecretBox.Password = _mainWindow.Settings.KrakenApiSecret;
        DataPathText.Text = _mainWindow.Settings.CustomDataPath ?? _mainWindow.StorageService.GetDataFolderPath();
        if (_mainWindow.Settings.CustomDataPath != null)
            ResetDataFolderBtn.Visibility = Visibility.Visible;
    }

    private void UpdateLedgerStatus()
    {
        if (_mainWindow == null) return;

        var fileDate = _mainWindow.StorageService.GetLedgerFileDate();
        if (fileDate.HasValue)
        {
            TradeStatusText.Text = $"{_mainWindow.Ledger.Count} ledger entries loaded from cache.";
            TradeFileDate.Text = $"Last downloaded: {fileDate.Value:dd MMM yyyy HH:mm}";
            ResumeInfoText.Text = "Click 'Download Ledger' to fetch only new entries since last download.";
        }
        else
        {
            TradeStatusText.Text = "No ledger data downloaded yet.";
            TradeFileDate.Text = "";
            ResumeInfoText.Text = "";
        }

        if (_mainWindow.Ledger.Count > 0)
        {
            var ledger = _mainWindow.Ledger;
            TotalTradesText.Text = $"Total ledger entries: {ledger.Count}";

            var earliest = ledger.Min(e => e.DateTime);
            var latest = ledger.Max(e => e.DateTime);
            DateRangeText.Text = $"Date range: {earliest:dd MMM yyyy} to {latest:dd MMM yyyy}";

            var typeCounts = ledger.GroupBy(e => e.Type)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}: {g.Count()}")
                .ToList();
            TaxYearsText.Text = $"Entry types: {string.Join(", ", typeCounts)}";

            var assets = ledger
                .Select(e => e.NormalisedAsset)
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct()
                .OrderBy(a => a)
                .ToList();
            AssetsText.Text = $"Assets: {string.Join(", ", assets)}";
        }
        else
        {
            TotalTradesText.Text = "";
            DateRangeText.Text = "";
            TaxYearsText.Text = "";
            AssetsText.Text = "";
        }
    }

    private void UpdateFxStatus()
    {
        if (_mainWindow?.FxService == null)
        {
            FxStatusText.Text = "No FX rates loaded.";
            return;
        }

        var stats = _mainWindow.FxService.GetCacheStats();
        if (stats.Count == 0)
        {
            FxStatusText.Text = "No FX rates loaded.";
        }
        else
        {
            var totalPoints = stats.Sum(s => s.DataPoints);
            var onDisk = stats.Count(s => s.OnDisk);
            FxStatusText.Text = $"{stats.Count} FX pairs loaded ({totalPoints:#,##0} data points, {onDisk} cached to disk).";
        }
    }

    private void UpdateFreshnessIndicator()
    {
        if (_mainWindow == null) return;

        var settings = _mainWindow.Settings;
        var now = DateTimeOffset.UtcNow;

        if (settings.LastLedgerDownload.HasValue)
        {
            var age = now - settings.LastLedgerDownload.Value;
            LedgerFreshnessText.Text = $"Ledger last downloaded: {settings.LastLedgerDownload.Value.LocalDateTime:dd/MM/yyyy HH:mm} ({FormatAge(age)} ago)";
        }
        else
        {
            LedgerFreshnessText.Text = "Ledger: never downloaded";
        }

        if (settings.LastFxDownload.HasValue)
        {
            var age = now - settings.LastFxDownload.Value;
            FxFreshnessText.Text = $"FX rates last downloaded: {settings.LastFxDownload.Value.LocalDateTime:dd/MM/yyyy HH:mm} ({FormatAge(age)} ago)";
        }
        else
        {
            FxFreshnessText.Text = "FX rates: never downloaded";
        }

        // Show warning if data is stale (more than 7 days)
        var ledgerStale = settings.LastLedgerDownload.HasValue && (now - settings.LastLedgerDownload.Value).TotalDays > 7;
        var fxStale = settings.LastFxDownload.HasValue && (now - settings.LastFxDownload.Value).TotalDays > 7;

        if (ledgerStale || fxStale)
        {
            FreshnessWarningText.Text = "Data may be stale. Consider re-downloading for up-to-date calculations.";
            FreshnessWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            FreshnessWarningText.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        _mainWindow.Settings.KrakenApiKey = ApiKeyBox.Text.Trim();
        _mainWindow.Settings.KrakenApiSecret = ApiSecretBox.Password.Trim();
        _mainWindow.KrakenService.SetCredentials(
            _mainWindow.Settings.KrakenApiKey,
            _mainWindow.Settings.KrakenApiSecret);

        TestConnectionBtn.IsEnabled = false;
        try
        {
            var rawResponse = await _mainWindow.KrakenService.TestConnectionAsync();

            if (rawResponse.Contains("\"error\":[]") || rawResponse.Contains("\"ledger\""))
            {
                InfoMessage.Message = "Connection successful! API key is working.";
                InfoMessage.Severity = InfoBarSeverity.Success;
            }
            else
            {
                InfoMessage.Message = $"Kraken responded: {rawResponse}";
                InfoMessage.Severity = InfoBarSeverity.Error;
            }
            InfoMessage.IsOpen = true;
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"Connection failed: {ex.Message}";
            InfoMessage.Severity = InfoBarSeverity.Error;
            InfoMessage.IsOpen = true;
        }
        finally
        {
            TestConnectionBtn.IsEnabled = true;
        }
    }

    private async void SaveCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        _mainWindow.Settings.KrakenApiKey = ApiKeyBox.Text.Trim();
        _mainWindow.Settings.KrakenApiSecret = ApiSecretBox.Password.Trim();
        _mainWindow.KrakenService.SetCredentials(
            _mainWindow.Settings.KrakenApiKey,
            _mainWindow.Settings.KrakenApiSecret);

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);

        InfoMessage.Message = "Credentials saved.";
        InfoMessage.Severity = InfoBarSeverity.Success;
        InfoMessage.IsOpen = true;
    }

    private async void DownloadTrades_Click(object sender, RoutedEventArgs e)
    {
        await DownloadLedgerAsync(resume: true);
    }

    private async void ResetTrades_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset Ledger Data",
            Content = "This will delete all cached ledger data AND FX rate cache, then re-download everything from scratch. Continue?",
            PrimaryButtonText = "Reset & Re-download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _mainWindow!.StorageService.DeleteLedgerAsync();
            _mainWindow.StorageService.DeleteFxCache();
            _mainWindow.SetLedger(new List<KrakenLedgerEntry>());
            _mainWindow.ResetFxService();
            UpdateLedgerStatus();
            UpdateFxStatus();
            await DownloadLedgerAsync(resume: false);
        }
    }

    private async System.Threading.Tasks.Task DownloadLedgerAsync(bool resume)
    {
        if (_mainWindow == null) return;

        if (string.IsNullOrWhiteSpace(_mainWindow.Settings.KrakenApiKey) ||
            string.IsNullOrWhiteSpace(_mainWindow.Settings.KrakenApiSecret))
        {
            InfoMessage.Message = "Please enter and save your API credentials first.";
            InfoMessage.Severity = InfoBarSeverity.Warning;
            InfoMessage.IsOpen = true;
            return;
        }

        SetButtonsEnabled(false);
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadStatusText.Visibility = Visibility.Visible;

        _cts = new CancellationTokenSource();

        try
        {
            double startTime = 0;
            int existingCount = 0;
            if (resume)
            {
                startTime = await _mainWindow.StorageService.GetLatestLedgerTimeAsync();
                if (startTime > 0)
                    existingCount = _mainWindow.Ledger.Count;
            }

            var progress = new Progress<(int count, string status)>(p =>
            {
                DownloadStatusText.Text = p.status;
            });

            var newEntries = await _mainWindow.KrakenService.DownloadLedgerAsync(startTime, progress, _cts.Token);

            List<KrakenLedgerEntry> allEntries;
            if (resume && startTime > 0)
            {
                allEntries = await _mainWindow.StorageService.MergeAndSaveLedgerAsync(newEntries);
                var addedCount = allEntries.Count - existingCount;
                InfoMessage.Message = addedCount > 0
                    ? $"Downloaded {addedCount} new ledger entries. Total: {allEntries.Count}."
                    : $"No new entries found. Total: {allEntries.Count} entries up to date.";
            }
            else
            {
                await _mainWindow.StorageService.SaveLedgerAsync(newEntries);
                allEntries = newEntries;
                InfoMessage.Message = $"Downloaded {allEntries.Count} ledger entries from scratch.";
            }

            _mainWindow.SetLedger(allEntries);
            UpdateLedgerStatus();

            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;

            // Automatically download FX rates after ledger download
            if (allEntries.Count > 0)
            {
                InfoMessage.Message += " Downloading FX rates...";
                FxProgress.Visibility = Visibility.Visible;
                FxProgress.IsIndeterminate = true;
                FxProgressText.Visibility = Visibility.Visible;

                var fxProgress = new Progress<(int count, string status)>(p =>
                {
                    FxProgressText.Text = p.status;
                });

                await _mainWindow.DownloadFxRatesAndRecalculateAsync(fxProgress, _cts!.Token);

                UpdateFxStatus();

                var stats = _mainWindow.FxService?.GetCacheStats();
                var pairCount = stats?.Count ?? 0;
                var pointCount = stats?.Sum(s => s.DataPoints) ?? 0;

                InfoMessage.Message = $"Ledger: {allEntries.Count} entries. FX rates: {pairCount} pairs, {pointCount:#,##0} data points. Tax calculations updated.";

                _mainWindow.Settings.LastLedgerDownload = DateTimeOffset.UtcNow;
                _mainWindow.Settings.LastFxDownload = DateTimeOffset.UtcNow;
                _mainWindow.AddAuditEntry("Download", $"Ledger: {allEntries.Count} entries, FX: {pairCount} pairs ({pointCount:#,##0} points)");
                await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
                UpdateFreshnessIndicator();

                FxProgress.Visibility = Visibility.Collapsed;
                FxProgress.IsIndeterminate = false;
                FxProgressText.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            InfoMessage.Message = "Download cancelled. Previously downloaded data is preserved.";
            InfoMessage.Severity = InfoBarSeverity.Informational;
            InfoMessage.IsOpen = true;
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"Download failed: {ex.Message}\nPreviously downloaded data is preserved.";
            InfoMessage.Severity = InfoBarSeverity.Error;
            InfoMessage.IsOpen = true;
        }
        finally
        {
            SetButtonsEnabled(true);
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadProgress.IsIndeterminate = false;
            DownloadStatusText.Visibility = Visibility.Collapsed;
        }
    }

    // ========== FX RATE DOWNLOAD ==========

    private async void DownloadFxRates_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        if (_mainWindow.Ledger.Count == 0)
        {
            InfoMessage.Message = "Download ledger data first before loading FX rates.";
            InfoMessage.Severity = InfoBarSeverity.Warning;
            InfoMessage.IsOpen = true;
            return;
        }

        SetButtonsEnabled(false);
        FxProgress.Visibility = Visibility.Visible;
        FxProgress.IsIndeterminate = true;
        FxProgressText.Visibility = Visibility.Visible;

        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(int count, string status)>(p =>
            {
                FxProgressText.Text = p.status;
            });

            // This creates the FxConversionService, downloads ALL needed rates, then recalculates
            FxProgressText.Text = "Preparing FX rate download...";
            await _mainWindow.DownloadFxRatesAndRecalculateAsync(progress, _cts.Token);

            UpdateFxStatus();
            UpdateLedgerStatus();

            var stats = _mainWindow.FxService?.GetCacheStats();
            var pairCount = stats?.Count ?? 0;
            var pointCount = stats?.Sum(s => s.DataPoints) ?? 0;

            InfoMessage.Message = $"FX rates loaded: {pairCount} pairs, {pointCount:#,##0} data points. Tax calculations updated.";
            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;

            _mainWindow.Settings.LastFxDownload = DateTimeOffset.UtcNow;
            _mainWindow.AddAuditEntry("FX Download", $"{pairCount} pairs, {pointCount:#,##0} data points");
            await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
            UpdateFreshnessIndicator();
        }
        catch (OperationCanceledException)
        {
            InfoMessage.Message = "FX rate download cancelled.";
            InfoMessage.Severity = InfoBarSeverity.Informational;
            InfoMessage.IsOpen = true;
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"FX rate download failed: {ex.Message}";
            InfoMessage.Severity = InfoBarSeverity.Error;
            InfoMessage.IsOpen = true;
        }
        finally
        {
            SetButtonsEnabled(true);
            FxProgress.Visibility = Visibility.Collapsed;
            FxProgress.IsIndeterminate = false;
            FxProgressText.Visibility = Visibility.Collapsed;
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        DownloadBtn.IsEnabled = enabled;
        ResetBtn.IsEnabled = enabled;
        FxDownloadBtn.IsEnabled = enabled;
    }

    private void LoadThemeSelector()
    {
        if (_mainWindow == null) return;
        var theme = _mainWindow.Settings.Theme;
        for (int i = 0; i < ThemeSelector.Items.Count; i++)
        {
            if (ThemeSelector.Items[i] is ComboBoxItem item && item.Tag?.ToString() == theme)
            {
                ThemeSelector.SelectedIndex = i;
                break;
            }
        }
    }

    private async void ThemeSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_mainWindow == null) return;
        if (ThemeSelector.SelectedItem is not ComboBoxItem item) return;

        var theme = item.Tag?.ToString() ?? "Default";
        _mainWindow.Settings.Theme = theme;
        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);

        // Apply theme to the root element
        if (_mainWindow.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private void LoadAuditLog()
    {
        if (_mainWindow == null) return;

        var searchText = AuditSearchBox?.Text?.Trim() ?? "";
        var entries = _mainWindow.Settings.AuditLog.AsEnumerable();

        if (!string.IsNullOrEmpty(searchText))
        {
            entries = entries.Where(e =>
                e.Action.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                e.Detail.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        var list = entries
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new AuditLogViewModel(e))
            .ToList();

        AuditLogList.ItemsSource = list;
        if (AuditLogCountText != null)
            AuditLogCountText.Text = $"Showing {list.Count} of {_mainWindow.Settings.AuditLog.Count} entries";
    }

    private void AuditSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        LoadAuditLog();
    }

    private async void ExportAuditLog_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null || _mainWindow.Settings.AuditLog.Count == 0) return;

        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "CryptoTax2026_AuditLog";
        picker.FileTypeChoices.Add("CSV File", new List<string> { ".csv" });

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var lines = new List<string> { "Timestamp,Action,Detail" };
        foreach (var entry in _mainWindow.Settings.AuditLog.OrderByDescending(e2 => e2.Timestamp))
        {
            lines.Add($"{entry.Timestamp.LocalDateTime:dd/MM/yyyy HH:mm:ss},\"{entry.Action}\",\"{entry.Detail.Replace("\"", "\"\"")}\"");
        }
        await File.WriteAllLinesAsync(file.Path, lines);
        BackupStatusText.Text = $"Audit log exported to {file.Path}";
    }

    private async void ClearAuditLog_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        _mainWindow.Settings.AuditLog.Clear();
        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        LoadAuditLog();
    }

    // ========== DATA FOLDER ==========

    private async void ChangeDataFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _mainWindow.Settings.CustomDataPath = folder.Path;
        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        DataPathText.Text = folder.Path;
        ResetDataFolderBtn.Visibility = Visibility.Visible;

        InfoMessage.Message = $"Custom data path set to {folder.Path}. Settings will be saved there on next restart.";
        InfoMessage.Severity = InfoBarSeverity.Success;
        InfoMessage.IsOpen = true;
    }

    private async void ResetDataFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        _mainWindow.Settings.CustomDataPath = null;
        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        DataPathText.Text = _mainWindow.StorageService.GetDataFolderPath();
        ResetDataFolderBtn.Visibility = Visibility.Collapsed;

        InfoMessage.Message = "Data path reset to default.";
        InfoMessage.Severity = InfoBarSeverity.Success;
        InfoMessage.IsOpen = true;
    }

    // ========== BACKUP / RESTORE ==========

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = $"CryptoTax2026_Backup_{DateTime.Now:yyyy-MM-dd_HHmmss}";
        picker.FileTypeChoices.Add("JSON File", new List<string> { ".json" });

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var json = JsonSerializer.Serialize(_mainWindow.Settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file.Path, json);

            _mainWindow.AddAuditEntry("Backup", $"Settings exported to {file.Path}");
            await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);

            BackupStatusText.Text = $"Backup saved to {file.Path}";
            InfoMessage.Message = "Backup exported successfully.";
            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"Backup failed: {ex.Message}";
            InfoMessage.Severity = InfoBarSeverity.Error;
            InfoMessage.IsOpen = true;
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            var json = await File.ReadAllTextAsync(file.Path);
            var restored = JsonSerializer.Deserialize<AppSettings>(json);
            if (restored == null)
            {
                InfoMessage.Message = "Invalid backup file.";
                InfoMessage.Severity = InfoBarSeverity.Error;
                InfoMessage.IsOpen = true;
                return;
            }

            // Confirm
            var dialog = new ContentDialog
            {
                Title = "Restore Backup",
                Content = $"This will replace all current settings with the backup from {file.Name}. " +
                    $"The backup contains {restored.ManualLedgerEntries.Count} manual entries, " +
                    $"{restored.CostBasisOverrides.Count} cost overrides, " +
                    $"{restored.DelistedAssets.Count} delisted assets. Continue?",
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            // Preserve window position from current settings
            restored.WindowX = _mainWindow.Settings.WindowX;
            restored.WindowY = _mainWindow.Settings.WindowY;
            restored.WindowWidth = _mainWindow.Settings.WindowWidth;
            restored.WindowHeight = _mainWindow.Settings.WindowHeight;
            restored.IsMaximized = _mainWindow.Settings.IsMaximized;

            await _mainWindow.StorageService.SaveSettingsAsync(restored);

            InfoMessage.Message = "Backup restored. Restart the app to apply all changes.";
            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;

            BackupStatusText.Text = $"Restored from {file.Name}";
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"Restore failed: {ex.Message}";
            InfoMessage.Severity = InfoBarSeverity.Error;
            InfoMessage.IsOpen = true;
        }
    }
}

public class AuditLogViewModel
{
    private readonly AuditLogEntry _entry;
    public AuditLogViewModel(AuditLogEntry entry) => _entry = entry;

    public string TimestampFormatted => _entry.Timestamp.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
    public string Action => _entry.Action;
    public string Detail => _entry.Detail;
}
