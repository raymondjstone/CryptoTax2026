using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using CryptoTax2026.Models;
using CryptoTax2026.Pages;
using CryptoTax2026.Services;

namespace CryptoTax2026;

public sealed partial class MainWindow : Window
{
    private readonly KrakenApiService _krakenService = new();
    private TradeStorageService _storageService = null!; // Initialized in LoadDataAsync with correct path

    private List<KrakenLedgerEntry> _ledger = new();
    private List<KrakenTrade> _trades = new(); // kept for export compatibility
    private List<TaxYearSummary> _taxYearSummaries = new();
    private List<CalculationWarning> _warnings = new();
    private AppSettings _settings = new();
    private FxConversionService? _fxService;
    private DelistedPriceService? _delistedPriceService;
    private Dictionary<string, Section104Pool> _finalPools = new();
    private CgtCalculationService? _lastCgtService; // cached for lightweight summary-only recalculation

    private AppWindow _appWindow = null!;

    private bool _coffeePromptShown = false;

    public MainWindow()
    {
        InitializeComponent();
        Title = "CryptoTax2026 - UK Crypto Capital Gains Tax Calculator";
        ExtendsContentIntoTitleBar = true;

        _appWindow = AppWindow.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this)));

        // Set the window/taskbar icon explicitly — required for unpackaged (MSI) installs
        // because MSIX package identity normally handles this automatically.
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
            _appWindow.SetIcon(iconPath);

        SetTitleBar(AppTitleBar);

        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;

        // Keyboard shortcuts
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(0)));
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(1)));
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number3, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(2)));
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number4, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(3)));
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number5, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(4)));
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number6, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(5)));
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number7, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(6)));
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number8, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(7)));
        Content.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Number9, VirtualKeyModifiers.Control, (_, _) => NavigateToTab(8)));

        LoadDataAsync();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        // Coffee prompt moved to end of LoadDataAsync() where UI is fully initialized
    }

    private async void LoadDataAsync()
    {
        try
        {
            // Check for custom data path configuration first
            var customDataPath = TradeStorageService.LoadCustomDataPath();
            _storageService = new TradeStorageService(customDataPath);

            _settings = await _storageService.LoadSettingsAsync();

            // Ensure consistency between config file and settings
            if (!string.IsNullOrEmpty(customDataPath) && string.IsNullOrEmpty(_settings.CustomDataPath))
            {
                _settings.CustomDataPath = customDataPath;
                await _storageService.SaveSettingsAsync(_settings);
            }
            else if (string.IsNullOrEmpty(customDataPath) && !string.IsNullOrEmpty(_settings.CustomDataPath))
            {
                // Update storage service if settings has a custom path but config file doesn't
                _storageService = new TradeStorageService(_settings.CustomDataPath);
                TradeStorageService.SaveCustomDataPath(_settings.CustomDataPath);
            }

            // Track first app use if not already set (AFTER determining final storage location)
            if (!_settings.FirstAppUse.HasValue)
            {
                _settings.FirstAppUse = DateTimeOffset.UtcNow;
                await _storageService.SaveSettingsAsync(_settings);
            }

            _krakenService.SetCredentials(_settings.KrakenApiKey ?? "", _settings.KrakenApiSecret ?? "");

            // Load delisted pairs price dataset (used as FX fallback and for delist dates)
            _delistedPriceService = DelistedPriceService.TryLoad();

            // Apply saved theme
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = _settings.Theme switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }

            RestoreWindowPosition();

            if (_storageService.HasSavedLedger())
            {
                _ledger = await _storageService.LoadLedgerAsync();
                await RecalculateWithCachedRatesAsync();
            }

            ContentFrame.Navigate(typeof(SettingsPage), this);
            NavView.SelectedItem = NavView.MenuItems[0];

            // Check for app updates (non-blocking)
            await CheckForUpdateAsync();

            // Show coffee prompt after UI is fully loaded
            if (!_coffeePromptShown)
            {
                _coffeePromptShown = true;
                ShowCoffeePromptIfNeeded();
            }
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title = "Startup Error",
                Content = ex.ToString(),
                CloseButtonText = "Close"
            };
            dlg.XamlRoot = ContentFrame?.XamlRoot;
            if (dlg.XamlRoot != null)
                await dlg.ShowAsync();
        }
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
        _fxService = new FxConversionService(_krakenService, _warnings, _storageService.GetDataFolderPath(), _settings.FxRateType, _delistedPriceService);
        _fxService.LoadAllFromDiskCache();

        var currencies = _ledger
            .Select(e => e.NormalisedAsset)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        var earliest = _ledger.Min(e => e.DateTime);
        var latest = _ledger.Max(e => e.DateTime);

        progress?.Report((0, $"Checking FX rates for {currencies.Count} currencies..."));

        // Extend any pairs whose cache doesn't reach today — balance snapshots reference
        // tax-year-end dates that go beyond the latest ledger entry.
        await _fxService.PreloadRatesAsync(currencies, earliest, progress, ct);

        // Now recalculate with the loaded rates
        progress?.Report((0, "Calculating capital gains..."));

        var fxService = _fxService;
        var warnings = _warnings;
        var trades = _trades;
        var delistedAssets = _settings.EffectiveDelistedAssets;
        var costOverrides = _settings.CostBasisOverrides;
        var mergedLedger = GetMergedLedger();
        var userInputs = _settings.TaxYearInputs;

        var (summaries, pools, cgtService) = await Task.Run(() =>
        {
            var svc = new CgtCalculationService(fxService, warnings, trades, delistedAssets, costOverrides);
            var results = svc.CalculateAllTaxYears(mergedLedger, userInputs);
            return (results, svc.FinalPools, svc);
        });

        _taxYearSummaries = summaries;
        _finalPools = pools;
        _lastCgtService = cgtService;

        RebuildTabs();
    }

    /// <summary>
    /// Recalculates tax on startup using only the disk cache — no network calls.
    /// pairmap.json restores which cacheKey each asset actually uses (Kraken or CryptoCompare),
    /// so LoadAllFromDiskCache picks up the correct files.
    /// If rates are missing or stale, the user can click "Download FX Rates".
    /// </summary>
    private async Task RecalculateWithCachedRatesAsync()
    {
        if (_ledger.Count == 0) return;

        var warnings = new List<CalculationWarning>();
        var fxService = new FxConversionService(_krakenService, warnings, _storageService.GetDataFolderPath(), _settings.FxRateType, _delistedPriceService);
        fxService.LoadAllFromDiskCache();

        var currencies = _ledger
            .Select(e => e.NormalisedAsset)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .ToList();
        fxService.SetActiveCurrencies(currencies);

        var trades = _trades;
        var delistedAssets = _settings.DelistedAssets;
        var costOverrides = _settings.CostBasisOverrides;
        var mergedLedger = GetMergedLedger();
        var userInputs = _settings.TaxYearInputs;

        var (summaries, pools, cgtService) = await Task.Run(() =>
        {
            var svc = new CgtCalculationService(fxService, warnings, trades, delistedAssets, costOverrides);
            var results = svc.CalculateAllTaxYears(mergedLedger, userInputs);
            return (results, svc.FinalPools, svc);
        });

        _fxService = fxService;
        _warnings = warnings;
        _taxYearSummaries = summaries;
        _finalPools = pools;
        _lastCgtService = cgtService;

        RebuildTabs();
    }

    /// <summary>
    /// Recalculates tax years using the already-loaded FX rates.
    /// Called by TaxYearPage when user inputs change (taxable income, other gains).
    /// </summary>
    public async Task RecalculateAndBuildTabsAsync()
    {
        if (_ledger.Count == 0 || _fxService == null) return;

        var fxService = _fxService;
        var warnings = _warnings;
        var trades = _trades;
        var delistedAssets = _settings.EffectiveDelistedAssets;
        var costOverrides = _settings.CostBasisOverrides;
        var mergedLedger = GetMergedLedger();
        var userInputs = _settings.TaxYearInputs;

        var (summaries, pools, cgtService) = await Task.Run(() =>
        {
            var svc = new CgtCalculationService(fxService, warnings, trades, delistedAssets, costOverrides);
            var results = svc.CalculateAllTaxYears(mergedLedger, userInputs);
            return (results, svc.FinalPools, svc);
        });

        _taxYearSummaries = summaries;
        _finalPools = pools;
        _lastCgtService = cgtService;

        RebuildTabs();
    }

    /// <summary>
    /// Lightweight recalculation that only rebuilds tax year summaries (CGT amounts,
    /// loss carry-forward) without redoing disposal matching or FX lookups.
    /// Use when only taxable income or other capital gains change.
    /// Falls back to full recalculation if no cached data is available.
    /// </summary>
    public async Task RecalculateSummariesOnlyAsync()
    {
        if (_lastCgtService == null)
        {
            await RecalculateAndBuildTabsAsync();
            return;
        }

        var cgtService = _lastCgtService;
        var userInputs = _settings.TaxYearInputs;

        var summaries = await Task.Run(() => cgtService.RebuildSummariesOnly(userInputs));

        if (summaries == null)
        {
            await RecalculateAndBuildTabsAsync();
            return;
        }

        _taxYearSummaries = summaries;
        // Pools and tabs don't change — just update the summaries
    }

    private void RebuildTabs()
    {
        // Remove dynamic tabs (keep Settings, Delisted Assets, FX Rates, Ledger, CSV Import at indices 0-4)
        while (NavView.MenuItems.Count > 5)
            NavView.MenuItems.RemoveAt(5);

        if (_finalPools.Count > 0)
        {
            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = "Holdings",
                Tag = "Holdings",
                Icon = new SymbolIcon(Symbol.AllApps)
            });
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

        if (_finalPools.Count > 0 && _taxYearSummaries.Count > 0)
        {
            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = "Tools",
                Tag = "Tools",
                Icon = new SymbolIcon(Symbol.Repair)
            });
        }

        // Tax year tabs last
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
            else if (tag == "Ledger")
            {
                ContentFrame.Navigate(typeof(LedgerPage), this);
            }
            else if (tag == "DelistedAssets")
            {
                ContentFrame.Navigate(typeof(DelistedAssetsPage), this);
            }
            else if (tag == "CsvImport")
            {
                ContentFrame.Navigate(typeof(CsvImportPage), this);
            }
            else if (tag == "Holdings")
            {
                ContentFrame.Navigate(typeof(HoldingsPage), this);
            }
            else if (tag == "Tools")
            {
                ContentFrame.Navigate(typeof(ToolsPage), this);
            }
            else if (tag == "PnLSummary")
            {
                ContentFrame.Navigate(typeof(PnlSummaryPage), (this, _taxYearSummaries));
            }
            else if (tag == "FxRates")
            {
                ContentFrame.Navigate(typeof(FxRatesPage), (this, _fxService));
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

    /// <summary>
    /// Returns the Kraken ledger merged with any manually imported entries (CSV imports).
    /// </summary>
    private List<KrakenLedgerEntry> GetMergedLedger()
    {
        var merged = new List<KrakenLedgerEntry>(_ledger);
        foreach (var manual in _settings.ManualLedgerEntries)
        {
            merged.Add(new KrakenLedgerEntry
            {
                RefId = manual.RefId,
                Time = manual.Date.ToUnixTimeSeconds(),
                Type = manual.Type,
                SubType = "",
                Asset = manual.Asset,
                AmountStr = manual.Amount.ToString(),
                FeeStr = manual.Fee.ToString(),
                BalanceStr = "0",
                LedgerId = manual.RefId,
                NormalisedAsset = manual.NormalisedAsset
            });
        }
        return merged;
    }

    public void AddAuditEntry(string action, string detail)
    {
        _settings.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            Detail = detail
        });
        // Keep last 500 entries
        if (_settings.AuditLog.Count > 500)
            _settings.AuditLog.RemoveRange(0, _settings.AuditLog.Count - 500);
    }

    public void UpdateSettings(AppSettings newSettings)
    {
        _settings = newSettings;
        _krakenService.SetCredentials(_settings.KrakenApiKey ?? "", _settings.KrakenApiSecret ?? "");

        // Apply theme
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = _settings.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    public async Task UpdateDataPathAsync(string? newCustomPath)
    {
        var oldPath = _storageService.GetDataFolderPath();

        // Create new storage service with the new path
        var newStorageService = new TradeStorageService(newCustomPath);
        var newPath = newStorageService.GetDataFolderPath();

        if (oldPath != newPath)
        {
            // Migrate data from old location to new location
            await newStorageService.MigrateDataAsync(oldPath);

            // Update storage service
            _storageService = newStorageService;

            // Save settings to new location
            _settings.CustomDataPath = newCustomPath;
            await _storageService.SaveSettingsAsync(_settings);

            // Reload ledger from new location if it exists
            if (_storageService.HasSavedLedger())
            {
                _ledger = await _storageService.LoadLedgerAsync();
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
    public DelistedPriceService? DelistedPriceService => _delistedPriceService;
    public Dictionary<string, Section104Pool> FinalPools => _finalPools;

    private static KeyboardAccelerator CreateAccelerator(VirtualKey key, VirtualKeyModifiers modifiers,
        Windows.Foundation.TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accel = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accel.Invoked += handler;
        return accel;
    }

    private void NavigateToTab(int index)
    {
        if (index >= 0 && index < NavView.MenuItems.Count)
            NavView.SelectedItem = NavView.MenuItems[index];
    }

    /// <summary>
    /// Public method to navigate to the Settings page from other pages.
    /// </summary>
    public void NavigateToSettings()
    {
        NavView.SelectedItem = NavView.MenuItems[0]; // Settings page
        ContentFrame.Navigate(typeof(SettingsPage), this);
    }

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

    private async void BuyMeCoffeeButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.BuyMeCoffeeClicked = true;
        _settings.LastCoffeePrompt = DateTimeOffset.UtcNow;
        await _storageService.SaveSettingsAsync(_settings);
    }

    private async Task CheckForUpdateAsync()
    {
        var update = await UpdateCheckService.CheckForUpdateAsync();
        if (update == null)
            return;

        var (newVersion, downloadUrl) = update.Value;
        var current = UpdateCheckService.GetCurrentVersion();

        var dialog = new ContentDialog
        {
            Title = "Update Available",
            Content = $"A new version of CryptoTax2026 is available.\n\nCurrent version: {current}\nLatest version: {newVersion}\n\nWould you like to download it?",
            PrimaryButtonText = "Download",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (s, e) =>
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(downloadUrl));
        };
        dialog.XamlRoot = ContentFrame.XamlRoot;
        if (dialog.XamlRoot == null) return;
        await dialog.ShowAsync();
    }

    public async void ShowCoffeePromptIfNeeded()
    {
        if (_settings.BuyMeCoffeeClicked)
            return;

        var now = DateTimeOffset.UtcNow;

        // Don't show for the first 2 hours after first app use
        if (_settings.FirstAppUse.HasValue && (now - _settings.FirstAppUse.Value).TotalHours < 2)
            return;

        if (_settings.LastCoffeePrompt.HasValue && (now - _settings.LastCoffeePrompt.Value).TotalDays < 14)
            return;

        _settings.LastCoffeePrompt = now;
        await _storageService.SaveSettingsAsync(_settings);

        var dialog = new ContentDialog
        {
            Title = "Support CryptoTax2026",
            Content = "If you find this app useful, please consider buying me a coffee! If you click the button below, you won't see this message again (even if you don't contribute).\n\n☕ Buy me a coffee: https://buymeacoffee.com/raymondjstone",
            CloseButtonText = "Close",
            PrimaryButtonText = "Open Buy Me a Coffee",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (s, e) =>
        {
            BuyMeCoffeeButton_Click(null!, null!);
            var uri = new Uri("https://buymeacoffee.com/raymondjstone");
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        };
        dialog.XamlRoot = ContentFrame.XamlRoot;
        if (dialog.XamlRoot == null) return;
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Updates the FX rate calculation method and recalculates all conversions.
    /// This ensures HMRC compliance with the selected rate type.
    /// </summary>
    public async Task UpdateFxRateTypeAsync(FxRateType newRateType)
    {
        _settings.FxRateType = newRateType;
        if (_fxService != null)
        {
            _fxService.SetRateType(newRateType);
        }

        // Save the setting
        await _storageService.SaveSettingsAsync(_settings);

        // Recalculate with new rate type
        await RecalculateWithCachedRatesAsync();

        AddAuditEntry("FX Rate Type", $"Changed rate calculation method to {newRateType}");
    }
}
