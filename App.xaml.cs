using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoTax2026.Services;
using CryptoTax2026.Models;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CryptoTax2026
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var crashLog = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CryptoTax2026", "crash.log");
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLog)!);
                System.IO.File.AppendAllText(crashLog,
                    $"[{DateTime.Now:O}] {e.Exception.GetType().FullName}: {e.Exception.Message}\n{e.Exception.StackTrace}\n\n");
            }
            catch { }
            e.Handled = true;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var commandLineArgs = Environment.GetCommandLineArgs();

            // Check for --SyncData command line argument
            if (commandLineArgs.Contains("--SyncData", StringComparer.OrdinalIgnoreCase))
            {
                await RunSyncDataModeAsync();
                Current.Exit();
                return;
            }

            // Check for --Backup {dir} command line argument
            var backupFlagIndex = Array.FindIndex(commandLineArgs,
                a => a.Equals("--Backup", StringComparison.OrdinalIgnoreCase));
            if (backupFlagIndex >= 0 && backupFlagIndex + 1 < commandLineArgs.Length)
            {
                var backupDir = commandLineArgs[backupFlagIndex + 1];
                await RunBackupModeAsync(backupDir);
                Current.Exit();
                return;
            }

            var splash = new SplashHostWindow();
            splash.Activate();

            // Fixed splash duration to avoid race condition with settings loading
            await ShowMainWindowWithSplashAsync(splash, 2000);
        }

        private async Task ShowMainWindowWithSplashAsync(Window splash, int splashMs)
        {
            await Task.Delay(splashMs);
            _window = new MainWindow();
            _window.Activate();
            splash.Close();
        }

        /// <summary>
        /// Runs the application in headless sync mode - downloads ledger and FX data, then exits.
        /// Used for scheduled automation to keep data current.
        /// </summary>
        private async Task RunSyncDataModeAsync()
        {
            Console.WriteLine("CryptoTax2026: Starting sync mode...");

            try
            {
                // Check for custom data path configuration
                var customDataPath = TradeStorageService.LoadCustomDataPath();

                // Create storage service with custom path if configured
                var storageService = new TradeStorageService(customDataPath);
                var settings = await storageService.LoadSettingsAsync();

                // If custom path is configured but not yet set in settings, update it
                if (!string.IsNullOrEmpty(customDataPath) && string.IsNullOrEmpty(settings.CustomDataPath))
                {
                    settings.CustomDataPath = customDataPath;
                    await storageService.SaveSettingsAsync(settings);
                }
                // If settings has custom path but config file doesn't, create it
                else if (string.IsNullOrEmpty(customDataPath) && !string.IsNullOrEmpty(settings.CustomDataPath))
                {
                    TradeStorageService.SaveCustomDataPath(settings.CustomDataPath);
                }

                // Validate API credentials
                if (string.IsNullOrEmpty(settings.KrakenApiKey) || string.IsNullOrEmpty(settings.KrakenApiSecret))
                {
                    Console.WriteLine("Error: Kraken API credentials not configured. Please configure them in the UI first.");
                    return;
                }

                var krakenService = new KrakenApiService();
                krakenService.SetCredentials(settings.KrakenApiKey, settings.KrakenApiSecret);

                // Test API connection
                Console.WriteLine("Testing Kraken API connection...");
                try
                {
                    await krakenService.TestConnectionAsync();
                    Console.WriteLine("✓ Kraken API connection successful");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ Kraken API connection failed: {ex.Message}");
                    return;
                }

                // Download ledger data
                Console.WriteLine("Downloading ledger data...");
                var existingLedger = storageService.HasSavedLedger() 
                    ? await storageService.LoadLedgerAsync() 
                    : new List<KrakenLedgerEntry>();

                var startTime = existingLedger.Count > 0 
                    ? existingLedger.Max(e => e.DateTime).ToUnixTimeSeconds() 
                    : 0;

                var progress = new Progress<(int count, string status)>((update) =>
                {
                    Console.WriteLine($"  {update.status}");
                });

                var newLedger = await krakenService.DownloadLedgerAsync(startTime, progress);

                if (newLedger.Count > 0)
                {
                    // Merge with existing data
                    var existingIds = new HashSet<string>(existingLedger.Select(e => e.LedgerId), StringComparer.OrdinalIgnoreCase);
                    var newEntries = newLedger.Where(e => !existingIds.Contains(e.LedgerId)).ToList();

                    existingLedger.AddRange(newEntries);
                    existingLedger.Sort((a, b) => a.DateTime.CompareTo(b.DateTime));

                    await storageService.SaveLedgerAsync(existingLedger);
                    Console.WriteLine($"✓ Ledger updated: {newEntries.Count} new entries, {existingLedger.Count} total entries");
                }
                else
                {
                    Console.WriteLine("✓ Ledger is up to date");
                }

                // Download FX rates
                if (existingLedger.Count > 0)
                {
                    Console.WriteLine("Downloading FX rates...");

                    var warnings = new List<CalculationWarning>();
                    var fxService = new FxConversionService(krakenService, warnings, storageService.GetDataFolderPath(), settings.FxRateType);
                    fxService.LoadAllFromDiskCache();

                    var currencies = existingLedger
                        .Select(e => e.NormalisedAsset)
                        .Where(a => !string.IsNullOrEmpty(a))
                        .Distinct()
                        .OrderBy(a => a)
                        .ToList();

                    var earliest = existingLedger.Min(e => e.DateTime);

                    var fxProgress = new Progress<(int count, string status)>((update) =>
                    {
                        Console.WriteLine($"  {update.status}");
                    });

                    await fxService.PreloadRatesAsync(currencies, earliest, fxProgress);
                    Console.WriteLine($"✓ FX rates updated for {currencies.Count} currencies");

                    if (warnings.Count > 0)
                    {
                        Console.WriteLine($"⚠ {warnings.Count} warnings during FX rate download:");
                        foreach (var warning in warnings)
                        {
                            Console.WriteLine($"  - {warning.Message}");
                        }
                    }
                }

                Console.WriteLine("✓ Sync completed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Sync failed: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Runs the application in headless backup mode — loads the saved ledger and FX rates
        /// from disk, runs the CGT calculation, exports all tax years to an Excel file in
        /// <paramref name="backupDir"/>, then exits.  No network calls are made.
        /// </summary>
        private async Task RunBackupModeAsync(string backupDir)
        {
            Console.WriteLine($"CryptoTax2026: Starting backup export to '{backupDir}'...");

            try
            {
                var customDataPath = TradeStorageService.LoadCustomDataPath();
                var storageService = new TradeStorageService(customDataPath);
                var settings = await storageService.LoadSettingsAsync();

                if (!storageService.HasSavedLedger())
                {
                    Console.WriteLine("Error: No ledger data found. Please sync data first.");
                    return;
                }

                Console.WriteLine("Loading ledger...");
                var ledger = await storageService.LoadLedgerAsync();
                Console.WriteLine($"✓ Loaded {ledger.Count} ledger entries");

                Console.WriteLine("Loading FX rates from cache...");
                var warnings = new List<CalculationWarning>();
                var delistedPrices = DelistedPriceService.TryLoad();
                var fxService = new FxConversionService(
                    new KrakenApiService(), warnings, storageService.GetDataFolderPath(), settings.FxRateType,
                    delistedPrices);
                fxService.LoadAllFromDiskCache();
                Console.WriteLine("✓ FX rates loaded from cache");

                Console.WriteLine("Calculating tax years...");
                var summaries = await Task.Run(() =>
                {
                    var svc = new CgtCalculationService(
                        fxService, warnings,
                        new List<KrakenTrade>(),
                        settings.EffectiveDelistedAssets,
                        settings.CostBasisOverrides);
                    return svc.CalculateAllTaxYears(ledger, settings.TaxYearInputs);
                });
                Console.WriteLine($"✓ Calculated {summaries.Count} tax year(s)");

                if (warnings.Count > 0)
                    Console.WriteLine($"⚠ {warnings.Count} warning(s) during calculation");

                System.IO.Directory.CreateDirectory(backupDir);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var excelPath = System.IO.Path.Combine(backupDir, $"CryptoTax_backup_{timestamp}.xlsx");

                Console.WriteLine($"Exporting to '{excelPath}'...");
                var exportService = new ExportService();
                exportService.ExportAllYearsToExcel(excelPath, summaries);
                Console.WriteLine($"✓ Export complete: {excelPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Backup failed: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
    }
}
