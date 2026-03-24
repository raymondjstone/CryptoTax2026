using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CryptoTax2026.Models;
using CryptoTax2026.Pages;
using CryptoTax2026.Services;

namespace CryptoTax2026;

public sealed partial class MainWindow : Window
{
    private readonly TradeStorageService _storageService = new();
    private readonly KrakenApiService _krakenService = new();
    private readonly CgtCalculationService _cgtService = new();

    private List<KrakenTrade> _trades = new();
    private List<TaxYearSummary> _taxYearSummaries = new();
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "CryptoTax2026 - UK Crypto Capital Gains Tax Calculator";
        ExtendsContentIntoTitleBar = true;

        LoadDataAsync();
    }

    private async void LoadDataAsync()
    {
        _settings = await _storageService.LoadSettingsAsync();
        _krakenService.SetCredentials(_settings.KrakenApiKey, _settings.KrakenApiSecret);

        if (_storageService.HasSavedTrades())
        {
            _trades = await _storageService.LoadTradesAsync();
            RecalculateAndBuildTabs();
        }

        // Navigate to settings by default
        ContentFrame.Navigate(typeof(SettingsPage), this);
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    public void OnTradesDownloaded(List<KrakenTrade> trades)
    {
        _trades = trades;
        RecalculateAndBuildTabs();
    }

    public void RecalculateAndBuildTabs()
    {
        if (_trades.Count == 0) return;

        _taxYearSummaries = _cgtService.CalculateAllTaxYears(_trades, _settings.TaxYearInputs);

        // Remove old tax year tabs (keep Settings tab at index 0)
        while (NavView.MenuItems.Count > 1)
            NavView.MenuItems.RemoveAt(1);

        // Add a tab for each tax year
        foreach (var summary in _taxYearSummaries.OrderBy(s => s.StartYear))
        {
            var item = new NavigationViewItem
            {
                Content = summary.TaxYear,
                Tag = summary.TaxYear,
                Icon = new SymbolIcon(Symbol.Calculator)
            };
            NavView.MenuItems.Add(item);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            if (tag == "Settings")
            {
                ContentFrame.Navigate(typeof(SettingsPage), this);
            }
            else if (tag != null)
            {
                var summary = _taxYearSummaries.FirstOrDefault(s => s.TaxYear == tag);
                if (summary != null)
                {
                    ContentFrame.Navigate(typeof(TaxYearPage), (this, summary));
                }
            }
        }
    }

    public KrakenApiService KrakenService => _krakenService;
    public TradeStorageService StorageService => _storageService;
    public AppSettings Settings => _settings;
    public List<KrakenTrade> Trades => _trades;
    public List<TaxYearSummary> TaxYearSummaries => _taxYearSummaries;
}
