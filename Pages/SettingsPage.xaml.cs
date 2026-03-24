using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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
        }
    }

    private void LoadSettings()
    {
        if (_mainWindow == null) return;

        ApiKeyBox.Text = _mainWindow.Settings.KrakenApiKey;
        ApiSecretBox.Password = _mainWindow.Settings.KrakenApiSecret;
        DataPathText.Text = _mainWindow.StorageService.GetDataFolderPath();
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
            Content = "This will delete all cached ledger data and re-download everything from scratch. Continue?",
            PrimaryButtonText = "Reset & Re-download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _mainWindow!.StorageService.DeleteLedgerAsync();
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

        DownloadBtn.IsEnabled = false;
        ResetBtn.IsEnabled = false;
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

            // Now recalculate (this also downloads FX rates)
            DownloadStatusText.Text = "Calculating gains and loading FX rates...";
            _mainWindow.OnLedgerDownloaded(allEntries);
            UpdateLedgerStatus();

            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;
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
            DownloadBtn.IsEnabled = true;
            ResetBtn.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadProgress.IsIndeterminate = false;
            DownloadStatusText.Visibility = Visibility.Collapsed;
        }
    }
}
