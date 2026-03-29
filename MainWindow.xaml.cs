using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
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
    private FxConversionService? _fxService;

    private AppWindow _appWindow = null!;

    public MainWindow()
    {
        InitializeComponent();
        Title = "CryptoTax2026 - UK Crypto Capital Gains Tax Calculator";
        ExtendsContentIntoTitleBar = true;

        _appWindow = AppWindow.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this)));

        SetTitleBar(AppTitleBar);

        Closed += MainWindow_Closed;

        LoadDataAsync();
    }

    private async void LoadDataAsync()
    {
        _settings = await _storageService.LoadSettingsAsync();
        _krakenService.SetCredentials(_settings.KrakenApiKey, _settings.KrakenApiSecret);

        RestoreWindowPosition();

        if (_storageService.HasSavedLedger())
        {
            _ledger = await _storageService.LoadLedgerAsync();

            // On startup, try to recalculate using cached FX rates only (no downloads)
            // User can click "Download FX Rates" to do a full download
            await RecalculateWithCachedRatesAsync();
        }

        ContentFrame.Navigate(typeof(SettingsPage), this);
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>
    /// Sets the ledger without triggering recalculation.
    /// SettingsPage calls this after ledger download, then triggers FX download separately.
    /// </summary>
    public void SetLedger(List<KrakenLedgerEntry> ledger)
    {
        _ledger = ledger;
    }

    /// <summary>
    /// Clears the in-memory FX service after a cache wipe, so the next calculation
    /// starts with a clean slate.
    /// </summary>
    public void ResetFxService()
    {
        _fxService = null;
        _taxYearSummaries = new List<TaxYearSummary>();
        RebuildTabs();
    }

    /// <summary>
    /// Downloads all needed FX rates for the current ledger, then recalculates tax years.
    /// This is the main entry point called by the "Download FX Rates" button.
    /// </summary>
    public async Task DownloadFxRatesAndRecalculateAsync(
        IProgress<(int count, string status)>? progress = null,
        CancellationToken ct = default)
    {
        if (_ledger.Count == 0) return;

        _warnings = new List<CalculationWarning>();

        // Create FX service — constructor restores _pairMap, then load all cached files
        _fxService = new FxConversionService(_krakenService, _warnings);
        _fxService.LoadAllFromDiskCache();

        var currencies = _ledger
            .Select(e => e.NormalisedAsset)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .ToList();

        var earliest = _ledger.Min(e => e.DateTime);
        var latest = _ledger.Max(e => e.DateTime);

        progress?.Report((0, $"Checking FX rates for {currencies.Count} currencies..."));

        // Extend any pairs whose cache doesn't reach today — balance snapshots reference
        // tax-year-end dates that go beyond the latest ledger entry.
        await _fxService.PreloadRatesAsync(currencies, earliest, progress, ct);

        // Now recalculate with the loaded rates
        progress?.Report((0, "Calculating capital gains..."));

        var cgtService = new CgtCalculationService(_fxService, _warnings, _trades, _settings.DelistedAssets);
        _taxYearSummaries = cgtService.CalculateAllTaxYears(_ledger, _settings.TaxYearInputs);

        RebuildTabs();
    }

    /// <summary>
    /// Recalculates tax on startup using only the disk cache — no network calls.
    /// pairmap.json restores which cacheKey each asset actually uses (Kraken or CryptoCompare),
    /// so LoadAllFromDiskCache picks up the correct files.
    /// If rates are missing or stale, the user can click "Download FX Rates".
    /// </summary>
    private Task RecalculateWithCachedRatesAsync()
    {
        if (_ledger.Count == 0) return Task.CompletedTask;

        _warnings = new List<CalculationWarning>();
        _fxService = new FxConversionService(_krakenService, _warnings);
        _fxService.LoadAllFromDiskCache();

        var cgtService = new CgtCalculationService(_fxService, _warnings, _trades, _settings.DelistedAssets);
        _taxYearSummaries = cgtService.CalculateAllTaxYears(_ledger, _settings.TaxYearInputs);

        RebuildTabs();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recalculates tax years using the already-loaded FX rates.
    /// Called by TaxYearPage when user inputs change (taxable income, other gains).
    /// </summary>
    public async Task RecalculateAndBuildTabsAsync()
    {
        if (_ledger.Count == 0 || _fxService == null) return;

        _warnings = new List<CalculationWarning>();

        var cgtService = new CgtCalculationService(_fxService, _warnings, _trades, _settings.DelistedAssets);
        _taxYearSummaries = cgtService.CalculateAllTaxYears(_ledger, _settings.TaxYearInputs);

        RebuildTabs();
        await Task.CompletedTask;
    }

    private void RebuildTabs()
    {
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

        if (_taxYearSummaries.Count > 0)
        {
            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = "P&L Summary",
                Tag = "PnLSummary",
                Icon = new SymbolIcon(Symbol.Library)
            });
        }

        if (_fxService != null)
        {
            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = "FX Rates",
                Tag = "FxRates",
                Icon = new SymbolIcon(Symbol.Globe)
            });
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
            else if (tag == "PnLSummary")
            {
                ContentFrame.Navigate(typeof(PnlSummaryPage), (this, _taxYearSummaries));
            }
            else if (tag == "FxRates")
            {
                ContentFrame.Navigate(typeof(FxRatesPage), (this, _fxService!));
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
    public FxConversionService? FxService => _fxService;

    private void RestoreWindowPosition()
    {
        if (_settings.IsMaximized)
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
            return;
        }

        if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue
            && _settings.WindowWidth.Value > 100 && _settings.WindowHeight.Value > 100)
        {
            _appWindow.Resize(new SizeInt32(_settings.WindowWidth.Value, _settings.WindowHeight.Value));
        }

        if (_settings.WindowX.HasValue && _settings.WindowY.HasValue)
        {
            _appWindow.Move(new PointInt32(_settings.WindowX.Value, _settings.WindowY.Value));
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        var isMaximized = _appWindow.Presenter is OverlappedPresenter op
            && op.State == OverlappedPresenterState.Maximized;

        _settings.IsMaximized = isMaximized;

        if (!isMaximized)
        {
            _settings.WindowX = _appWindow.Position.X;
            _settings.WindowY = _appWindow.Position.Y;
            _settings.WindowWidth = _appWindow.Size.Width;
            _settings.WindowHeight = _appWindow.Size.Height;
        }

        await _storageService.SaveSettingsAsync(_settings);
    }
}
