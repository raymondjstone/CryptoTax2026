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
            LoadFxRateTypeSelector();
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
            var krakenPairs = _mainWindow.FxService.GetDiscoveredKrakenPairs();
            var krakenPairCount = krakenPairs.Count;

            FxStatusText.Text = $"{stats.Count} FX pairs loaded ({totalPoints:#,##0} data points, {onDisk} cached to disk). " +
                               $"{krakenPairCount} pairs discovered from Kraken API.";
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

    // New method for reset and re-download FX rates
    private async void ResetAndDownloadFxRates_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        if (_mainWindow.Ledger.Count == 0)
        {
            InfoMessage.Message = "Download ledger data first before loading FX rates.";
            InfoMessage.Severity = InfoBarSeverity.Warning;
            InfoMessage.IsOpen = true;
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Reset FX Rate Data",
            Content = "This will delete all cached FX rate data and re-download fresh rates from scratch.\n\n" +
                     "✅ PRESERVED (NEVER DELETED):\n" +
                     "• Manual ledger entries\n" +
                     "• Manual FX rate overrides\n" +
                     "• All settings and configurations\n" +
                     "• Your ledger data\n\n" +
                     "❌ DELETED (FX cache only):\n" +
                     "• Cached exchange rate files\n" +
                     "• Pair discovery cache\n\n" +
                     "Continue?",
            PrimaryButtonText = "Reset & Re-download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        SetButtonsEnabled(false);
        FxProgress.Visibility = Visibility.Visible;
        FxProgress.IsIndeterminate = true;
        FxProgressText.Visibility = Visibility.Visible;

        _cts = new CancellationTokenSource();

        try
        {
            // Reset FX cache and service
            _mainWindow.StorageService.DeleteFxCache();
            _mainWindow.ResetFxService();
            UpdateFxStatus();

            var progress = new Progress<(int count, string status)>(p =>
            {
                FxProgressText.Text = p.status;
            });

            FxProgressText.Text = "Resetting FX cache and preparing fresh download...";
            await _mainWindow.DownloadFxRatesAndRecalculateAsync(progress, _cts.Token);

            UpdateFxStatus();
            UpdateLedgerStatus();

            var stats = _mainWindow.FxService?.GetCacheStats();
            var pairCount = stats?.Count ?? 0;
            var pointCount = stats?.Sum(s => s.DataPoints) ?? 0;

            InfoMessage.Message = $"FX rates reset and reloaded: {pairCount} pairs, {pointCount:#,##0} fresh data points. Tax calculations updated.";
            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;

            _mainWindow.Settings.LastFxDownload = DateTimeOffset.UtcNow;
            _mainWindow.AddAuditEntry("FX Reset & Download", $"{pairCount} pairs, {pointCount:#,##0} fresh data points");
            await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
            UpdateFreshnessIndicator();
        }
        catch (OperationCanceledException)
        {
            InfoMessage.Message = "FX rate reset and download cancelled.";
            InfoMessage.Severity = InfoBarSeverity.Informational;
            InfoMessage.IsOpen = true;
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"FX rate reset and download failed: {ex.Message}";
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
        FxResetAndDownloadBtn.IsEnabled = enabled;
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

    private void LoadFxRateTypeSelector()
    {
        if (_mainWindow == null) return;
        var rateType = _mainWindow.Settings.FxRateType.ToString();
        for (int i = 0; i < FxRateTypeSelector.Items.Count; i++)
        {
            if (FxRateTypeSelector.Items[i] is ComboBoxItem item && item.Tag?.ToString() == rateType)
            {
                FxRateTypeSelector.SelectedIndex = i;
                break;
            }
        }
    }

    private async void FxRateTypeSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_mainWindow == null) return;
        if (FxRateTypeSelector.SelectedItem is not ComboBoxItem item) return;

        var rateTypeStr = item.Tag?.ToString() ?? "Close";
        if (Enum.TryParse<FxRateType>(rateTypeStr, out var newRateType))
        {
            if (newRateType != _mainWindow.Settings.FxRateType)
            {
                await _mainWindow.UpdateFxRateTypeAsync(newRateType);

                InfoMessage.Message = $"Exchange rate calculation method changed to {newRateType}. All calculations updated.";
                InfoMessage.Severity = InfoBarSeverity.Success;
                InfoMessage.IsOpen = true;
            }
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

        try
        {
            // Update data path and migrate data immediately
            await _mainWindow.UpdateDataPathAsync(folder.Path);

            // Update UI
            DataPathText.Text = folder.Path;
            ResetDataFolderBtn.Visibility = Visibility.Visible;

            InfoMessage.Message = $"Data path changed to {folder.Path}. All data has been migrated to the new location.";
            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;

            // Update the status displays to reflect new location
            UpdateLedgerStatus();
            UpdateFxStatus();
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"Failed to change data path: {ex.Message}";
            InfoMessage.Severity = InfoBarSeverity.Error;
            InfoMessage.IsOpen = true;
        }
    }

    private async void ResetDataFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        try
        {
            // Reset to default path and migrate data immediately
            await _mainWindow.UpdateDataPathAsync(null);

            // Update UI
            DataPathText.Text = _mainWindow.StorageService.GetDataFolderPath();
            ResetDataFolderBtn.Visibility = Visibility.Collapsed;

            InfoMessage.Message = "Data path reset to default location. All data has been migrated back.";
            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;

            // Update the status displays to reflect new location
            UpdateLedgerStatus();
            UpdateFxStatus();
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"Failed to reset data path: {ex.Message}";
            InfoMessage.Severity = InfoBarSeverity.Error;
            InfoMessage.IsOpen = true;
        }
    }

    // ========== BACKUP / RESTORE ==========

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var backupFolderName = $"CryptoTax2026_Backup_{timestamp}";
            var backupPath = Path.Combine(folder.Path, backupFolderName);

            // Get backup statistics before starting
            var fileCount = _mainWindow.StorageService.GetDataFileCount();
            var totalSize = _mainWindow.StorageService.GetTotalDataSize();
            var sizeMB = totalSize / (1024.0 * 1024.0);

            // Confirm backup with user
            var dialog = new ContentDialog
            {
                Title = "Export Complete Backup",
                Content = $"This will create a complete backup of all your data:\n\n" +
                         $"• {fileCount} files ({sizeMB:F1} MB)\n" +
                         $"• Ledger data, settings, and FX rate cache\n" +
                         $"• Backup location: {backupPath}\n\n" +
                         $"Continue?",
                PrimaryButtonText = "Export Backup",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            // Perform the backup
            await _mainWindow.StorageService.BackupAllDataAsync(backupPath);

            _mainWindow.AddAuditEntry("Backup", $"Complete data backup exported to {backupPath} ({fileCount} files, {sizeMB:F1} MB)");
            await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);

            BackupStatusText.Text = $"Backup saved to {backupFolderName}";
            InfoMessage.Message = $"Complete backup exported successfully to {backupFolderName}";
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

        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        try
        {
            // Validate backup folder
            var backupFiles = Directory.GetFiles(folder.Path, "*.json", SearchOption.AllDirectories);
            if (backupFiles.Length == 0)
            {
                InfoMessage.Message = "Invalid backup folder. No data files found.";
                InfoMessage.Severity = InfoBarSeverity.Error;
                InfoMessage.IsOpen = true;
                return;
            }

            // Try to read settings from backup to show details
            var backupSettingsFile = Path.Combine(folder.Path, "settings.json");
            AppSettings? backupSettings = null;
            if (File.Exists(backupSettingsFile))
            {
                var json = await File.ReadAllTextAsync(backupSettingsFile);
                backupSettings = JsonSerializer.Deserialize<AppSettings>(json);
            }

            var fileCount = backupFiles.Length;
            var totalSize = backupFiles.Sum(f => new FileInfo(f).Length);
            var sizeMB = totalSize / (1024.0 * 1024.0);

            // Show comprehensive "are you sure" dialog
            var dialogContent = $"⚠️ WARNING: This will completely replace ALL your current data!\n\n" +
                               $"CURRENT DATA WILL BE LOST:\n" +
                               $"• All ledger data and tax calculations\n" +
                               $"• All settings and configurations\n" +
                               $"• All FX rate cache\n\n" +
                               $"BACKUP TO RESTORE:\n" +
                               $"• Location: {folder.Name}\n" +
                               $"• Files: {fileCount} ({sizeMB:F1} MB)\n";

            if (backupSettings != null)
            {
                dialogContent += $"• Manual entries: {backupSettings.ManualLedgerEntries.Count}\n" +
                               $"• Cost overrides: {backupSettings.CostBasisOverrides.Count}\n" +
                               $"• Delisted assets: {backupSettings.DelistedAssets.Count}\n";
            }

            dialogContent += $"\nThis action CANNOT be undone. Are you absolutely sure?";

            var confirmDialog = new ContentDialog
            {
                Title = "⚠️ Restore Complete Backup",
                Content = dialogContent,
                PrimaryButtonText = "Yes, Replace All Data",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary) return;

            // Second confirmation for safety
            var finalConfirmDialog = new ContentDialog
            {
                Title = "Final Confirmation",
                Content = "Last chance! This will permanently delete all your current data and replace it with the backup.\n\nProceed with restore?",
                PrimaryButtonText = "Proceed",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await finalConfirmDialog.ShowAsync() != ContentDialogResult.Primary) return;

            // Perform the restore
            await _mainWindow.StorageService.RestoreAllDataAsync(folder.Path);

            // Reload settings from restored data
            var restoredSettings = await _mainWindow.StorageService.LoadSettingsAsync();

            // Preserve current window position
            restoredSettings.WindowX = _mainWindow.Settings.WindowX;
            restoredSettings.WindowY = _mainWindow.Settings.WindowY;
            restoredSettings.WindowWidth = _mainWindow.Settings.WindowWidth;
            restoredSettings.WindowHeight = _mainWindow.Settings.WindowHeight;
            restoredSettings.IsMaximized = _mainWindow.Settings.IsMaximized;

            await _mainWindow.StorageService.SaveSettingsAsync(restoredSettings);

            // Update current session
            _mainWindow.UpdateSettings(restoredSettings);

            // Reload ledger data from restored files
            if (_mainWindow.StorageService.HasSavedLedger())
            {
                var restoredLedger = await _mainWindow.StorageService.LoadLedgerAsync();
                _mainWindow.SetLedger(restoredLedger);
            }
            else
            {
                _mainWindow.SetLedger(new List<KrakenLedgerEntry>());
            }

            // Reset FX service to pick up restored cache
            _mainWindow.ResetFxService();

            // Refresh all UI elements
            LoadSettings();
            LoadThemeSelector();
            LoadFxRateTypeSelector();
            UpdateLedgerStatus();
            UpdateFxStatus();
            UpdateFreshnessIndicator();
            LoadAuditLog();

            _mainWindow.AddAuditEntry("Restore", $"Complete data backup restored from {folder.Name} ({fileCount} files, {sizeMB:F1} MB)");

            InfoMessage.Message = $"Backup restored successfully! All data has been replaced from {folder.Name}.";
            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;

            BackupStatusText.Text = $"Restored from {folder.Name}";
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
