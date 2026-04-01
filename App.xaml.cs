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
            Microsoft.Windows.ApplicationModel.WindowsAppRuntime.DeploymentManager.Initialize();
            InitializeComponent();
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
    }
}
