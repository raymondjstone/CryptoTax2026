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
            UpdateTradeStatus();
        }
    }

    private void LoadSettings()
    {
        if (_mainWindow == null) return;

        ApiKeyBox.Text = _mainWindow.Settings.KrakenApiKey;
        ApiSecretBox.Password = _mainWindow.Settings.KrakenApiSecret;
        DataPathText.Text = _mainWindow.StorageService.GetDataFolderPath();
    }

    private void UpdateTradeStatus()
    {
        if (_mainWindow == null) return;

        var fileDate = _mainWindow.StorageService.GetTradesFileDate();
        if (fileDate.HasValue)
        {
            TradeStatusText.Text = $"{_mainWindow.Trades.Count} trades loaded from cache.";
            TradeFileDate.Text = $"Last downloaded: {fileDate.Value:dd MMM yyyy HH:mm}";
            ResumeInfoText.Text = "Click 'Download Trades' to fetch only new trades since last download.";
        }
        else
        {
            TradeStatusText.Text = "No trades downloaded yet.";
            TradeFileDate.Text = "";
            ResumeInfoText.Text = "";
        }

        if (_mainWindow.Trades.Count > 0)
        {
            var trades = _mainWindow.Trades;
            TotalTradesText.Text = $"Total trades: {trades.Count}";

            var earliest = trades.Min(t => t.DateTime);
            var latest = trades.Max(t => t.DateTime);
            DateRangeText.Text = $"Date range: {earliest:dd MMM yyyy} to {latest:dd MMM yyyy}";

            var taxYears = trades
                .Select(t => CgtCalculationService.GetTaxYearLabel(t.DateTime))
                .Distinct()
                .OrderBy(y => y)
                .ToList();
            TaxYearsText.Text = $"Tax years covered: {string.Join(", ", taxYears)}";

            var assets = trades
                .Select(t => t.BaseAsset)
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct()
                .OrderBy(a => a)
                .ToList();
            AssetsText.Text = $"Assets traded: {string.Join(", ", assets)}";
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

        // Save credentials first
        _mainWindow.Settings.KrakenApiKey = ApiKeyBox.Text.Trim();
        _mainWindow.Settings.KrakenApiSecret = ApiSecretBox.Password.Trim();
        _mainWindow.KrakenService.SetCredentials(
            _mainWindow.Settings.KrakenApiKey,
            _mainWindow.Settings.KrakenApiSecret);

        TestConnectionBtn.IsEnabled = false;
        try
        {
            var rawResponse = await _mainWindow.KrakenService.TestConnectionAsync();

            // Show the raw response so user can debug
            if (rawResponse.Contains("\"error\":[]") || rawResponse.Contains("\"trades\""))
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
        // Resume from where we left off
        await DownloadTradesAsync(resume: true);
    }

    private async void ResetTrades_Click(object sender, RoutedEventArgs e)
    {
        // Confirm before wiping
        var dialog = new ContentDialog
        {
            Title = "Reset Trade Data",
            Content = "This will delete all cached trades and re-download everything from scratch. Continue?",
            PrimaryButtonText = "Reset & Re-download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _mainWindow!.StorageService.DeleteTradesAsync();
            await DownloadTradesAsync(resume: false);
        }
    }

    private async System.Threading.Tasks.Task DownloadTradesAsync(bool resume)
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
            // Determine start time: resume from latest trade or start from zero
            double startTime = 0;
            int existingCount = 0;
            if (resume)
            {
                startTime = await _mainWindow.StorageService.GetLatestTradeTimeAsync();
                if (startTime > 0)
                    existingCount = _mainWindow.Trades.Count;
            }

            var progress = new Progress<(int count, string status)>(p =>
            {
                DownloadStatusText.Text = p.status;
            });

            var newTrades = await _mainWindow.KrakenService.DownloadTradesAsync(startTime, progress, _cts.Token);

            // Merge with existing or save fresh
            List<KrakenTrade> allTrades;
            if (resume && startTime > 0)
            {
                allTrades = await _mainWindow.StorageService.MergeAndSaveTradesAsync(newTrades);
                var addedCount = allTrades.Count - existingCount;
                InfoMessage.Message = addedCount > 0
                    ? $"Downloaded {addedCount} new trades. Total: {allTrades.Count}."
                    : $"No new trades found. Total: {allTrades.Count} trades up to date.";
            }
            else
            {
                await _mainWindow.StorageService.SaveTradesAsync(newTrades);
                allTrades = newTrades;
                InfoMessage.Message = $"Downloaded {allTrades.Count} trades from scratch.";
            }

            _mainWindow.OnTradesDownloaded(allTrades);
            UpdateTradeStatus();

            InfoMessage.Severity = InfoBarSeverity.Success;
            InfoMessage.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            InfoMessage.Message = "Download cancelled. Previously downloaded trades are preserved.";
            InfoMessage.Severity = InfoBarSeverity.Informational;
            InfoMessage.IsOpen = true;
        }
        catch (Exception ex)
        {
            InfoMessage.Message = $"Download failed: {ex.Message}\nPreviously downloaded trades are preserved.";
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
