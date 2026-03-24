using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    private List<KrakenLedgerEntry> _ledger = new();
    private List<KrakenTrade> _trades = new(); // kept for export compatibility
    private List<TaxYearSummary> _taxYearSummaries = new();
    private List<CalculationWarning> _warnings = new();
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

        if (_storageService.HasSavedLedger())
        {
            _ledger = await _storageService.LoadLedgerAsync();
            await RecalculateAndBuildTabsAsync();
        }

        ContentFrame.Navigate(typeof(SettingsPage), this);
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    public async void OnLedgerDownloaded(List<KrakenLedgerEntry> ledger)
    {
        _ledger = ledger;
        await RecalculateAndBuildTabsAsync();
    }

    public async System.Threading.Tasks.Task RecalculateAndBuildTabsAsync(
        IProgress<(int count, string status)>? progress = null,
        CancellationToken ct = default)
    {
        if (_ledger.Count == 0) return;

        _warnings = new List<CalculationWarning>();

        // Create FX service and preload rates
        var fxService = new FxConversionService(_krakenService, _warnings);

        // Gather all currencies we need rates for
        var currencies = _ledger
            .Select(e => e.NormalisedAsset)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .ToList();

        var earliest = _ledger.Min(e => e.DateTime);

        progress?.Report((0, "Loading FX rates..."));
        try
        {
            await fxService.PreloadRatesAsync(currencies, earliest, progress, ct);
        }
        catch (Exception ex)
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Warning,
                Category = "FX Rate",
                Message = $"FX rate preload incomplete: {ex.Message}. Some conversions may use fallback rates."
            });
        }

        progress?.Report((0, "Calculating capital gains..."));

        var cgtService = new CgtCalculationService(fxService, _warnings);
        _taxYearSummaries = cgtService.CalculateAllTaxYears(_ledger, _settings.TaxYearInputs);

        // Remove old tax year tabs (keep Settings tab at index 0)
        while (NavView.MenuItems.Count > 1)
            NavView.MenuItems.RemoveAt(1);

        foreach (var summary in _taxYearSummaries.OrderBy(s => s.StartYear))
        {
            var warningCount = summary.Warnings.Count(w => w.Level is WarningLevel.Warning or WarningLevel.Error);
            var label = warningCount > 0 ? $"{summary.TaxYear} ({warningCount} issues)" : summary.TaxYear;

            var item = new NavigationViewItem
            {
                Content = label,
                Tag = summary.TaxYear,
                Icon = new SymbolIcon(warningCount > 0 ? Symbol.Important : Symbol.Calculator)
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
    public List<KrakenLedgerEntry> Ledger => _ledger;
    public List<KrakenTrade> Trades => _trades;
    public List<TaxYearSummary> TaxYearSummaries => _taxYearSummaries;
    public List<CalculationWarning> Warnings => _warnings;
}
