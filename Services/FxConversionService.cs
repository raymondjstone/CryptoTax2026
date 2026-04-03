using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

/// <summary>
/// Converts any currency amount to GBP using historical rates.
/// Primary source: Kraken public OHLC API (daily candles).
/// Fallback source: CryptoCompare (hourly candles) for delisted/missing pairs.
/// Rates are cached permanently on disk — historical data never expires.
///
/// IMPORTANT: USDT is NOT USD. USDT → USD → GBP (two-step conversion).
/// </summary>
public class FxConversionService
{
    private readonly string _cacheFolder;
    private readonly string _manualOverridesFile;
    private readonly string _pairMapFile;

    private readonly KrakenApiService _krakenApi;
    private readonly List<CalculationWarning> _warnings;
    private readonly HttpClient _fallbackHttp;

    // Cache: key = pair/source key, value = sorted list of (unix_timestamp -> OHLC data)
    private readonly Dictionary<string, SortedList<long, OhlcCandle>> _rateCache = new(StringComparer.OrdinalIgnoreCase);

    // Runtime-discovered pair mappings: (asset, quoteCurrency) -> (cacheKey, invert)
    private readonly Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)> _pairMap = new();

    // Track data source per cache key
    private readonly Dictionary<string, string> _pairSources = new(StringComparer.OrdinalIgnoreCase);

    // Current FX rate calculation method for HMRC compliance
    private FxRateType _rateType = FxRateType.Average;

    // Dynamic pair mappings discovered from Kraken API
    // Format: (asset, quoteCurrency) -> (krakenPairName, invert)
    // This replaces the hardcoded KnownPairs array with live data from Kraken
    private readonly Dictionary<(string Asset, string Quote), (string KrakenPair, bool Invert)> _krakenDiscoveredPairs = new();

    // Cache file for discovered Kraken pairs to avoid repeated API calls
    private readonly string _krakenPairsFile;

    // Aliases: when an asset can't be found under its normalised name, try these alternatives.
    // Covers renamed coins (POL←MATIC) and alternate Kraken tickers.
    private static readonly Dictionary<string, string[]> CoinAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POL"]    = new[] { "MATIC" },        // POL was MATIC — delisted on Kraken, use CryptoCompare
        ["MATIC"]  = new[] { "POL" },
        ["FET"]    = new[] { "ASI" },          // FET merged into ASI Alliance
        ["OCEAN"]  = new[] { "ASI" },
        ["RENDER"] = new[] { "RNDR" },
        ["RNDR"]   = new[] { "RENDER" },
    };

    // Track which assets we've already warned about to avoid duplicate warnings
    private readonly HashSet<string> _warnedAssets = new(StringComparer.OrdinalIgnoreCase);

    // Manual overrides: asset → sorted timestamp → GBP rate (same structure as _rateCache)
    private readonly Dictionary<string, SortedList<long, decimal>> _manualOverrides = new(StringComparer.OrdinalIgnoreCase);

    // Delisted-pairs CSV fallback — provides OHLC prices when Kraken API has no data
    private readonly DelistedPriceService? _delistedPrices;

    // Cache keys and currencies actually needed for the most-recently loaded ledger.
    // When non-empty, GetCacheStats() filters to only the relevant pairs.
    private HashSet<string> _activeCacheKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _activeCurrencies = new(StringComparer.OrdinalIgnoreCase);
    // CSV pair altnames (GBP and USD-quoted only) needed for the current ledger.
    private HashSet<string> _activeCsvPairNames = new(StringComparer.OrdinalIgnoreCase);

    public FxConversionService(KrakenApiService krakenApi, List<CalculationWarning> warnings, string? dataFolder = null, FxRateType rateType = FxRateType.Average, DelistedPriceService? delistedPrices = null)
    {
        _krakenApi = krakenApi;
        _warnings = warnings;
        _rateType = rateType;

        var baseFolder = dataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoTax2026");

        _cacheFolder = Path.Combine(baseFolder, "fx_cache");
        _manualOverridesFile = Path.Combine(_cacheFolder, "manual_overrides.json");
        _pairMapFile = Path.Combine(_cacheFolder, "pairmap.json");
        _krakenPairsFile = Path.Combine(_cacheFolder, "kraken_pairs.json");

        Directory.CreateDirectory(_cacheFolder);

        _delistedPrices = delistedPrices;
        _fallbackHttp = new HttpClient();
        LoadManualOverrides();
        _fallbackHttp.DefaultRequestHeaders.Add("User-Agent", "CryptoTax2026/1.0");

        // Warn when the CSV dataset doesn't extend to the current tax year end
        if (_delistedPrices != null && _delistedPrices.IsDataStale(out var taxYearEnd))
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Info,
                Category = "Delisted Prices",
                Message = $"Delisted pairs price data (kraken_delisted.csv) extends to " +
                          $"{_delistedPrices.LatestDataDate:dd/MM/yyyy}, before the tax year end " +
                          $"({taxYearEnd:dd/MM/yyyy}). FX prices for any pair that was still " +
                          $"trading after that date will be missing from the CSV fallback."
            });
        }

        // Load any previously discovered Kraken pairs from cache
        LoadKrakenDiscoveredPairs();

        // Populate initial pair map from discovered Kraken pairs
        PopulatePairMapFromDiscoveredPairs();

        // Restore dynamically discovered pairs from previous sessions (CryptoCompare, etc.)
        LoadPairMap();
    }

    /// <summary>
    /// Updates the FX rate calculation method for HMRC compliance.
    /// This affects how rates are extracted from OHLC data for all future conversions.
    /// </summary>
    public void SetRateType(FxRateType rateType)
    {
        _rateType = rateType;
    }

    /// <summary>
    /// Records which currency pairs are relevant for the current ledger so that
    /// <see cref="GetCacheStats"/> can suppress unrelated pairs.
    /// Call this after <see cref="LoadAllFromDiskCache"/> on the startup (cache-only) path.
    /// </summary>
    public void SetActiveCurrencies(IEnumerable<string> rawCurrencies)
    {
        var needed = rawCurrencies
            .Select(c => KrakenLedgerEntry.NormaliseAssetName(c).ToUpperInvariant())
            .Where(c => c != "GBP")
            .Distinct()
            .ToList();
        UpdateActiveSets(needed);
    }

    private void UpdateActiveSets(IEnumerable<string> neededCurrencies)
    {
        _activeCurrencies = new HashSet<string>(neededCurrencies, StringComparer.OrdinalIgnoreCase);
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GBPUSD" };
        var activeCsvPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var currency in neededCurrencies)
        {
            if (_pairMap.TryGetValue((currency, "GBP"), out var gp)) activeKeys.Add(gp.CacheKey);
            if (_pairMap.TryGetValue((currency, "USD"), out var up)) activeKeys.Add(up.CacheKey);
            if (_delistedPrices != null)
            {
                var csvGbp = _delistedPrices.GetPairAltname(currency, "GBP");
                if (csvGbp != null) activeCsvPairs.Add(csvGbp);
                var csvUsd = _delistedPrices.GetPairAltname(currency, "USD");
                if (csvUsd != null) activeCsvPairs.Add(csvUsd);
            }
        }
        _activeCacheKeys = activeKeys;
        _activeCsvPairNames = activeCsvPairs;
    }

    /// <summary>
    /// Pre-loads all FX rate data needed for the given set of currencies and date range.
    /// First discovers available pairs from Kraken API, then tries loading data.
    /// </summary>
    public async Task PreloadRatesAsync(
        IEnumerable<string> currencies,
        DateTimeOffset earliest,
        IProgress<(int count, string status)>? progress = null,
        CancellationToken ct = default,
        bool cacheOnly = false,
        DateTimeOffset latestNeeded = default)
    {
        if (latestNeeded == default)
            latestNeeded = DateTimeOffset.UtcNow;

        // First, refresh Kraken pairs if needed (only updates once per day)
        if (!cacheOnly)
        {
            progress?.Report((0, "Discovering available trading pairs from Kraken..."));
            await RefreshKrakenPairsAsync(ct);
        }

        var neededCurrencies = currencies
            .Select(c => KrakenLedgerEntry.NormaliseAssetName(c).ToUpperInvariant())
            .Where(c => c != "GBP")
            .Distinct()
            .ToList();

        // Collect all Kraken pairs we need to download
        var pairsToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always need USD→GBP for fallback conversions
        pairsToDownload.Add("GBPUSD");

        foreach (var currency in neededCurrencies)
        {
            if (currency is "USD" or "EUR")
            {
                if (_pairMap.TryGetValue((currency, "GBP"), out var fx))
                    pairsToDownload.Add(fx.CacheKey);
            }
            else if (currency is "USDT" or "USDC" or "DAI")
            {
                if (_pairMap.TryGetValue((currency, "USD"), out var stableFx))
                    pairsToDownload.Add(stableFx.CacheKey);
                pairsToDownload.Add("GBPUSD");
            }
            else
            {
                // For crypto: add both GBP and USD pairs if available
                // This ensures we have fallbacks when GBP pairs don't exist on Kraken
                bool hasGbpPair = _pairMap.TryGetValue((currency, "GBP"), out var gbpPair);
                bool hasUsdPair = _pairMap.TryGetValue((currency, "USD"), out var usdPair);

                if (hasGbpPair)
                {
                    pairsToDownload.Add(gbpPair.CacheKey);
                }

                if (hasUsdPair)
                {
                    pairsToDownload.Add(usdPair.CacheKey);
                    pairsToDownload.Add("GBPUSD");
                }
                // else: unknown — will try dynamic discovery + fallback below
            }
        }

        // Download known pairs first - process in alphabetical order
        int loaded = 0;
        int totalPairs = pairsToDownload.Count;
        foreach (var pair in pairsToDownload.OrderBy(p => p).ToList()) // Sort pairs alphabetically
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report((loaded, $"Loading FX rates: {pair} ({loaded + 1}/{totalPairs})..."));
            bool downloaded = false;
            try
            {
                downloaded = await EnsurePairLoadedAsync(pair, earliest, latestNeeded, ct, cacheOnly);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Log but continue — one bad pair shouldn't kill the whole download
                _warnings.Add(new CalculationWarning
                {
                    Level = WarningLevel.Warning,
                    Category = "FX Rate",
                    Message = $"Failed to load {pair}: {ex.Message}. Will try fallback."
                });
            }
            loaded++;
            // Only pause between requests when a network call was actually made
            if (downloaded) await Task.Delay(1500, ct);
        }

        if (!cacheOnly)
        {
            // Dynamic discovery for currencies we don't have rates for yet - process in alphabetical order
            var undiscoveredCurrencies = neededCurrencies
                .Where(c => c is not "USD" and not "EUR" and not "USDT" and not "USDC" and not "DAI")
                .Where(c => !_pairMap.ContainsKey((c, "GBP")) && !_pairMap.ContainsKey((c, "USD")))
                .OrderBy(c => c) // Ensure alphabetical order
                .ToList();

            foreach (var currency in undiscoveredCurrencies)
            {
                if (ct.IsCancellationRequested) break;
                progress?.Report((loaded, $"Discovering FX pair for {currency}..."));

                // Step 1: Try direct Kraken discovery (GBP and USD pairs)
                var discovered = await TryDiscoverPairAsync(currency, earliest, ct);
                if (discovered)
                {
                    loaded++;
                    continue;
                }

                // Step 2: Try aliases on Kraken (e.g. POL → try MATIC pairs)  
                bool foundViaAlias = false;
                if (CoinAliases.TryGetValue(currency, out var aliases))
                {
                    foreach (var alias in aliases)
                    {
                        if (ct.IsCancellationRequested) break;
                        progress?.Report((loaded, $"Trying Kraken alias {alias} for {currency}..."));

                        var aliasFound = await TryDiscoverPairForAliasAsync(currency, alias, earliest, ct);
                        if (aliasFound)
                        {
                            loaded++;
                            foundViaAlias = true;
                            break;
                        }
                    }
                }

                // Step 3: Only if no Kraken pairs found (GBP or USD), try CryptoCompare
                if (!foundViaAlias && !_pairMap.ContainsKey((currency, "GBP")) && !_pairMap.ContainsKey((currency, "USD")))
                {
                    progress?.Report((loaded, $"No Kraken pairs found for {currency}, trying CryptoCompare..."));
                    var fallbackOk = await TryFallbackCryptoCompareAsync(currency, earliest, ct);
                    if (fallbackOk)
                        loaded++;
                }

                await Task.Delay(500, ct);
            }

            // Clean up: Remove the old section that tried CryptoCompare as backup for failed Kraken currencies
            // This is no longer needed since we now prioritize Kraken USD pairs over CryptoCompare
        }

        UpdateActiveSets(neededCurrencies);

        progress?.Report((loaded, $"FX rates loaded for {loaded} pairs."));

        // Persist the full pair map so dynamic discoveries survive app restarts
        if (!cacheOnly)
            SavePairMap();
    }

    /// <summary>
    /// Discovers all available trading pairs from Kraken API and caches them.
    /// This replaces the hardcoded pair list with live data from Kraken.
    /// Only updates if the cache is older than 24 hours to avoid excessive API calls.
    /// </summary>
    public async Task RefreshKrakenPairsAsync(CancellationToken ct = default, bool force = false)
    {
        // Check if we need to refresh (only once per day unless forced)
        if (!force && File.Exists(_krakenPairsFile))
        {
            var lastModified = File.GetLastWriteTime(_krakenPairsFile);
            if (DateTime.Now - lastModified < TimeSpan.FromHours(24))
                return; // Cache is still fresh
        }

        try
        {
            var allPairs = await _krakenApi.GetAssetPairsAsync(ct);

            // Filter for pairs that are useful for FX conversion (GBP, USD, EUR quotes)
            var targetQuotes = new[] { "ZUSD", "ZGBP", "ZEUR", "USD", "GBP", "EUR", "USDT", "USDC", "DAI" };
            var discoveredPairs = new Dictionary<(string Asset, string Quote), (string KrakenPair, bool Invert)>();

            foreach (var (pairName, pairInfo) in allPairs)
            {
                if (!pairInfo.IsActive) continue;

                var baseAsset = NormalizeAssetName(pairInfo.BaseAsset);
                var quoteAsset = NormalizeAssetName(pairInfo.QuoteAsset);

                // Skip if not a target quote currency
                if (!targetQuotes.Contains(pairInfo.QuoteAsset, StringComparer.OrdinalIgnoreCase))
                    continue;

                var key = (baseAsset, quoteAsset);

                // Handle GBP/USD pair specially (needs inversion for USD→GBP)
                if (baseAsset == "GBP" && quoteAsset == "USD")
                {
                    discoveredPairs[("USD", "GBP")] = (pairName, true);
                }
                else
                {
                    discoveredPairs[key] = (pairName, false);
                }
            }

            // Update discovered pairs and save to cache
            _krakenDiscoveredPairs.Clear();
            foreach (var kv in discoveredPairs)
                _krakenDiscoveredPairs[kv.Key] = kv.Value;

            SaveKrakenDiscoveredPairs();

            // Update the main pair map with new discoveries
            PopulatePairMapFromDiscoveredPairs();

            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Info,
                Category = "FX Rate",
                Message = $"Discovered {discoveredPairs.Count} trading pairs from Kraken API for FX conversion."
            });
        }
        catch (Exception ex)
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Warning,
                Category = "FX Rate",
                Message = $"Failed to refresh Kraken pairs: {ex.Message}. Using cached pairs."
            });
        }
    }

    /// <summary>
    /// Normalizes asset names from Kraken API format to our standard format
    /// </summary>
    private static string NormalizeAssetName(string krakenAssetName)
    {
        // Convert Kraken's asset names to standard names
        return krakenAssetName.ToUpperInvariant() switch
        {
            "ZUSD" => "USD",
            "ZGBP" => "GBP", 
            "ZEUR" => "EUR",
            "ZJPY" => "JPY",
            "ZCAD" => "CAD",
            "ZAUD" => "AUD",
            "XXBT" => "BTC",
            "XETH" => "ETH", 
            "XXRP" => "XRP",
            "XLTC" => "LTC",
            "XXLM" => "XLM",
            "XXDG" => "DOGE",
            "XZEC" => "ZEC",
            "XMLN" => "MLN",
            "XXMR" => "XMR",
            "XREP" => "REP",
            "XETC" => "ETC",
            _ => krakenAssetName.ToUpperInvariant()
        };
    }

    /// <summary>
    /// Populates the main _pairMap from discovered Kraken pairs.
    /// Kraken discoveries always take priority over old cached entries.
    /// </summary>
    private void PopulatePairMapFromDiscoveredPairs()
    {
        foreach (var kv in _krakenDiscoveredPairs)
        {
            // Always update with fresh Kraken discoveries - they take priority over old cache
            _pairMap[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// Saves discovered Kraken pairs to cache file
    /// </summary>
    private void SaveKrakenDiscoveredPairs()
    {
        try
        {
            var data = new
            {
                lastUpdated = DateTimeOffset.UtcNow,
                pairs = _krakenDiscoveredPairs.ToDictionary(
                    kv => $"{kv.Key.Asset}|{kv.Key.Quote}",
                    kv => new { krakenPair = kv.Value.KrakenPair, invert = kv.Value.Invert })
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_krakenPairsFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Loads previously discovered Kraken pairs from cache file  
    /// </summary>
    private void LoadKrakenDiscoveredPairs()
    {
        if (!File.Exists(_krakenPairsFile)) return;

        try
        {
            var json = File.ReadAllText(_krakenPairsFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("pairs", out var pairsElement))
            {
                foreach (var prop in pairsElement.EnumerateObject())
                {
                    var parts = prop.Name.Split('|');
                    if (parts.Length != 2) continue;

                    var key = (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
                    var krakenPair = prop.Value.GetProperty("krakenPair").GetString() ?? "";
                    var invert = prop.Value.GetProperty("invert").GetBoolean();

                    if (!string.IsNullOrEmpty(krakenPair))
                        _krakenDiscoveredPairs[key] = (krakenPair, invert);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Tries to find a working Kraken pair for an unknown asset by trying common pair name patterns.
    /// </summary>
    private async Task<bool> TryDiscoverPairAsync(string asset, DateTimeOffset earliest, CancellationToken ct)
    {
        var candidates = new List<(string Pair, string Quote, bool Invert)>
        {
            ($"{asset}GBP", "GBP", false),
            ($"{asset}USD", "USD", false),
            ($"X{asset}ZGBP", "GBP", false),
            ($"X{asset}ZUSD", "USD", false),
        };

        foreach (var (pairName, quote, invert) in candidates)
        {
            try
            {
                var loaded = await TryDownloadKrakenPairAsync(pairName, earliest, ct);
                if (loaded)
                {
                    _pairMap[(asset, quote)] = (pairName, invert);
                    _pairSources[pairName] = "Kraken";
                    if (quote == "USD")
                        await EnsurePairLoadedAsync("GBPUSD", earliest, DateTimeOffset.UtcNow, ct);
                    return true;
                }
            }
            catch
            {
                // Pair doesn't exist on Kraken, try next
            }
            await Task.Delay(1500, ct);
        }

        return false;
    }

    /// <summary>
    /// Tries Kraken pair discovery using an alias name, then registers the result under the original asset.
    /// e.g. asset=POL, alias=MATIC → tries MATICGBP, MATICUSD etc.
    /// </summary>
    private async Task<bool> TryDiscoverPairForAliasAsync(string asset, string alias, DateTimeOffset earliest, CancellationToken ct)
    {
        var candidates = new List<(string Pair, string Quote, bool Invert)>
        {
            ($"{alias}GBP", "GBP", false),
            ($"{alias}USD", "USD", false),
            ($"X{alias}ZGBP", "GBP", false),
            ($"X{alias}ZUSD", "USD", false),
        };

        foreach (var (pairName, quote, invert) in candidates)
        {
            try
            {
                var loaded = await TryDownloadKrakenPairAsync(pairName, earliest, ct);
                if (loaded)
                {
                    // Register under the ORIGINAL asset name so lookups for POL find MATICGBP data
                    _pairMap[(asset, quote)] = (pairName, invert);
                    _pairSources[pairName] = $"Kraken (via {alias})";
                    if (quote == "USD")
                        await EnsurePairLoadedAsync("GBPUSD", earliest, DateTimeOffset.UtcNow, ct);
                    return true;
                }
            }
            catch { }
            await Task.Delay(1500, ct);
        }

        return false;
    }

    /// <summary>
    /// Fallback: downloads daily rate data from CryptoCompare for assets Kraken doesn't have.
    /// Uses daily candles with normalized 00:00:00 timestamps to match Kraken format.
    /// Tries GBP first, then USD.
    /// </summary>
    private async Task<bool> TryFallbackCryptoCompareAsync(string asset, DateTimeOffset earliest, CancellationToken ct)
    {
        // Also try aliases for CryptoCompare — some coins have different tickers
        var tickersToTry = new List<string> { asset };
        if (CoinAliases.TryGetValue(asset, out var aliases))
            tickersToTry.AddRange(aliases);

        foreach (var ticker in tickersToTry)
        {
            // Try GBP first
            var gbpKey = $"CC_{ticker}_GBP";
            if (await TryDownloadCryptoCompareAsync(ticker, "GBP", gbpKey, earliest, ct))
            {
                _pairMap[(asset, "GBP")] = (gbpKey, false);
                var sourceLabel = ticker == asset ? "CryptoCompare" : $"CryptoCompare (via {ticker})";
                _pairSources[gbpKey] = sourceLabel;
                return true;
            }

            await Task.Delay(500, ct);

            // Try USD
            var usdKey = $"CC_{ticker}_USD";
            if (await TryDownloadCryptoCompareAsync(ticker, "USD", usdKey, earliest, ct))
            {
                _pairMap[(asset, "USD")] = (usdKey, false);
                var sourceLabel = ticker == asset ? "CryptoCompare" : $"CryptoCompare (via {ticker})";
                _pairSources[usdKey] = sourceLabel;
                await EnsurePairLoadedAsync("GBPUSD", earliest, DateTimeOffset.UtcNow, ct);
                return true;
            }

            await Task.Delay(500, ct);
        }

        // Nothing worked — warn
        if (_warnedAssets.Add(asset))
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Warning,
                Category = "FX Rate",
                Message = $"Could not find FX rates for {asset} from Kraken or CryptoCompare. " +
                          $"Conversions for this asset will be zero — values may be incorrect."
            });
        }
        return false;
    }

    /// <summary>
    /// Downloads daily OHLC data from CryptoCompare to match Kraken's daily format.
    /// Aggregates hourly data into daily candles with 00:00:00 timestamps for consistency.
    /// </summary>
    private async Task<bool> TryDownloadCryptoCompareAsync(
        string fromSymbol, string toSymbol, string cacheKey,
        DateTimeOffset earliest, CancellationToken ct, bool forceRefresh = false)
    {
        // Check memory + disk cache first (skip when forcing a refresh of stale data)
        if (!forceRefresh)
        {
            if (_rateCache.ContainsKey(cacheKey) && _rateCache[cacheKey].Count > 0)
                return true;
            if (TryLoadFromCache(cacheKey))
                return true;
        }

        // Use CryptoCompare histoday API for daily data to match Kraken format
        var allRates = new SortedList<long, OhlcCandle>();
        var toTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sinceTs = earliest.AddDays(-7).ToUnixTimeSeconds();
        int maxSegments = 10; // Reduced for daily data

        try
        {
            for (int seg = 0; seg < maxSegments && !ct.IsCancellationRequested; seg++)
            {
                // CryptoCompare histoday: daily candles like Kraken, max 2000 days per request
                var url = $"https://min-api.cryptocompare.com/data/v2/histoday" +
                          $"?fsym={fromSymbol}&tsym={toSymbol}&limit=2000&toTs={toTs}";

                var response = await _fallbackHttp.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return false;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Check for errors
                if (root.TryGetProperty("Response", out var resp) && resp.GetString() == "Error")
                    return false;

                if (!root.TryGetProperty("Data", out var dataOuter)) return false;
                if (!dataOuter.TryGetProperty("Data", out var dataArray)) return false;

                int added = 0;
                long earliestInBatch = long.MaxValue;
                foreach (var item in dataArray.EnumerateArray())
                {
                    var time = item.GetProperty("time").GetInt64();
                    var close = item.GetProperty("close").GetDecimal();
                    if (close > 0)
                    {
                        // Normalize timestamp to 00:00:00 of the day (like Kraken daily candles)
                        var dateTime = DateTimeOffset.FromUnixTimeSeconds(time);
                        var dayStart = new DateTimeOffset(dateTime.Year, dateTime.Month, dateTime.Day, 0, 0, 0, TimeSpan.Zero);
                        var normalizedTimestamp = dayStart.ToUnixTimeSeconds();

                        // CryptoCompare provides OHLC data, extract all values
                        var open = item.TryGetProperty("open", out var openProp) ? openProp.GetDecimal() : close;
                        var high = item.TryGetProperty("high", out var highProp) ? highProp.GetDecimal() : close;
                        var low = item.TryGetProperty("low", out var lowProp) ? lowProp.GetDecimal() : close;

                        allRates[normalizedTimestamp] = new OhlcCandle
                        {
                            Timestamp = normalizedTimestamp,
                            Open = open,
                            High = high,
                            Low = low,
                            Close = close
                        };
                        added++;
                        if (normalizedTimestamp < earliestInBatch) earliestInBatch = normalizedTimestamp;
                    }
                }

                if (added == 0) break;

                // If we've reached before the earliest needed date, stop
                if (earliestInBatch <= sinceTs) break;

                // Move window back by days, not hours
                var earliestDate = DateTimeOffset.FromUnixTimeSeconds(earliestInBatch);
                var previousDay = earliestDate.AddDays(-1);
                toTs = new DateTimeOffset(previousDay.Year, previousDay.Month, previousDay.Day, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds();

                await Task.Delay(300, ct); // CryptoCompare rate limit
            }
        }
        catch
        {
            // Download failed — return whatever we got
        }

        if (allRates.Count == 0)
            return false;

        _rateCache[cacheKey] = allRates;
        SaveToCache(cacheKey, allRates);
        return true;
    }

    /// <summary>
    /// Tries to download OHLC data for a Kraken pair. Returns true if data was found.
    /// </summary>
    private async Task<bool> TryDownloadKrakenPairAsync(string krakenPair, DateTimeOffset earliest, CancellationToken ct)
    {
        if (_rateCache.ContainsKey(krakenPair) && _rateCache[krakenPair].Count > 0)
            return true;

        if (TryLoadFromCache(krakenPair))
            return true;

        // Snap back to the start of the UK tax year (6 April) that contains this date,
        // so every download covers complete tax years rather than partial ones.
        var sinceDate = earliest.AddDays(-7);
        int taxYearStartYear = (sinceDate.Month > 4 || (sinceDate.Month == 4 && sinceDate.Day >= 6))
            ? sinceDate.Year
            : sinceDate.Year - 1;
        long sinceUnixTime = new DateTimeOffset(taxYearStartYear, 4, 6, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var allCandles = new List<OhlcCandle>();

        var currentSince = sinceUnixTime;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var candles = await _krakenApi.GetOhlcDataAsync(krakenPair, currentSince, ct);

                if (candles.Count == 0)
                    break;

                allCandles.AddRange(candles);

                if (candles.Count < 720)
                    break;

                currentSince = candles.Last().Timestamp;
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException) { throw; } // let cancellation propagate
        catch
        {
            // Kraken returned an error (e.g. "Unknown asset pair") — this pair doesn't exist
            return false;
        }

        if (allCandles.Count == 0)
            return false;

        var rates = new SortedList<long, OhlcCandle>();
        foreach (var candle in allCandles)
            rates[candle.Timestamp] = candle;

        _rateCache[krakenPair] = rates;
        _pairSources[krakenPair] = "Kraken";
        SaveToCache(krakenPair, rates);
        return true;
    }

    /// <summary>
    /// Converts an amount from the given currency to GBP at the given date/time.
    /// </summary>
    public decimal ConvertToGbp(decimal amount, string fromCurrency, DateTimeOffset date)
    {
        fromCurrency = KrakenLedgerEntry.NormaliseAssetName(fromCurrency).ToUpperInvariant();

        if (fromCurrency == "GBP") return amount;
        if (amount == 0) return 0;

        var timestamp = date.ToUnixTimeSeconds();

        // Fiat
        if (fromCurrency == "USD")
            return amount * GetRate("USD", "GBP", timestamp);
        if (fromCurrency == "EUR")
            return amount * GetRate("EUR", "GBP", timestamp);

        // Stablecoins: → USD → GBP
        if (fromCurrency is "USDT" or "USDC" or "DAI")
        {
            var usdAmount = amount * GetRate(fromCurrency, "USD", timestamp);
            return usdAmount * GetRate("USD", "GBP", timestamp);
        }

        // Crypto: try direct GBP pair, fallback to USD pair + USD/GBP
        var gbpRate = TryGetRate(fromCurrency, "GBP", timestamp);
        if (gbpRate.HasValue)
            return amount * gbpRate.Value;

        var usdRate = TryGetRate(fromCurrency, "USD", timestamp);
        if (usdRate.HasValue)
        {
            var usdVal = amount * usdRate.Value;
            return usdVal * GetRate("USD", "GBP", timestamp);
        }

        // Fallback to delisted pairs CSV — covers periods where Kraken API has no data
        if (_delistedPrices != null)
        {
            var csvGbp = _delistedPrices.TryGetRate(fromCurrency, "GBP", timestamp);
            if (csvGbp != null)
                return amount * csvGbp.GetRate(_rateType);

            var csvUsd = _delistedPrices.TryGetRate(fromCurrency, "USD", timestamp);
            if (csvUsd != null)
                return amount * csvUsd.GetRate(_rateType) * GetRate("USD", "GBP", timestamp);

            var csvEur = _delistedPrices.TryGetRate(fromCurrency, "EUR", timestamp);
            if (csvEur != null)
                return amount * csvEur.GetRate(_rateType) * GetRate("EUR", "GBP", timestamp);
        }

        // Manual override: user-supplied pair rates (e.g. BTCGBP, KUSD), GBP/USD/EUR-quoted
        var manualResult = TryGetManualRate(fromCurrency, timestamp);
        if (manualResult.HasValue)
            return amount * manualResult.Value;

        // No rate available
        if (_warnedAssets.Add($"{fromCurrency}_{DateOnly.FromDateTime(date.UtcDateTime)}"))
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Error,
                Category = "FX Rate",
                Message = $"No GBP or USD exchange rate available for {fromCurrency} on {date:dd/MM/yyyy HH:mm}. " +
                          $"Using zero value — this asset needs a rate to calculate correctly.",
                Date = date,
                Asset = fromCurrency
            });
        }
        return 0m;
    }

    public decimal GetGbpValueOfAsset(string asset, decimal quantity, DateTimeOffset date)
        => ConvertToGbp(quantity, asset, date);

    private decimal GetRate(string from, string to, long timestamp)
    {
        var rate = TryGetRate(from, to, timestamp);
        if (rate.HasValue) return rate.Value;

        var warnKey = $"rate_{from}_{to}";
        if (_warnedAssets.Add(warnKey))
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Warning,
                Category = "FX Rate",
                Message = $"No {from}/{to} rate data loaded. Using fallback rate of 1.0 (likely inaccurate).",
                Date = DateTimeOffset.FromUnixTimeSeconds(timestamp),
                Asset = from
            });
        }
        return 1m;
    }

    private decimal? TryGetRate(string from, string to, long timestamp)
    {
        from = from.ToUpperInvariant();
        to = to.ToUpperInvariant();

        if (!_pairMap.TryGetValue((from, to), out var pair))
            return null;

        if (!_rateCache.TryGetValue(pair.CacheKey, out var rates) || rates.Count == 0)
            return null;

        var closestRate = FindClosestRate(rates, timestamp, _rateType);

        if (pair.Invert && closestRate != 0)
            return 1m / closestRate;

        return closestRate;
    }

    /// <summary>
    /// Looks up a user-supplied manual pair rate for <paramref name="fromCurrency"/> and
    /// converts it to a GBP value.  Checks GBP-quoted pairs first, then USD, then EUR.
    /// e.g. a stored key of "BTCGBP" gives a direct GBP rate;
    ///      "KUSD" (or "BTCUSD") is multiplied by the USD/GBP rate.
    /// </summary>
    private decimal? TryGetManualRate(string fromCurrency, long timestamp)
    {
        // GBP-quoted pairs → direct rate
        foreach (var suffix in (ReadOnlySpan<string>)["GBP", "ZGBP"])
        {
            if (_manualOverrides.TryGetValue($"{fromCurrency}{suffix}", out var r) && r.Count > 0)
            {
                var rate = FindClosestManualRate(r, timestamp);
                if (rate != 0) return rate;
            }
        }

        // USD-quoted pairs → convert via USD/GBP
        foreach (var suffix in (ReadOnlySpan<string>)["USD", "ZUSD"])
        {
            if (_manualOverrides.TryGetValue($"{fromCurrency}{suffix}", out var r) && r.Count > 0)
            {
                var rate = FindClosestManualRate(r, timestamp);
                if (rate != 0) return rate * GetRate("USD", "GBP", timestamp);
            }
        }

        // EUR-quoted pairs → convert via EUR/GBP
        foreach (var suffix in (ReadOnlySpan<string>)["EUR", "ZEUR"])
        {
            if (_manualOverrides.TryGetValue($"{fromCurrency}{suffix}", out var r) && r.Count > 0)
            {
                var rate = FindClosestManualRate(r, timestamp);
                if (rate != 0) return rate * GetRate("EUR", "GBP", timestamp);
            }
        }

        return null;
    }

    private static decimal FindClosestRate(SortedList<long, OhlcCandle> rates, long targetTimestamp, FxRateType rateType)
    {
        var keys = rates.Keys;
        int lo = 0, hi = keys.Count - 1;
        int bestBefore = -1;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] <= targetTimestamp)
            {
                bestBefore = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        OhlcCandle? bestCandle = null;

        // For daily candles, we need to find the candle that represents the same trading day
        // A transaction at any time during day X should use day X's OHLC candle
        // Daily candles have timestamps at 00:00:00 UTC representing the start of that day's trading

        var targetDate = DateTimeOffset.FromUnixTimeSeconds(targetTimestamp);
        var targetDayStart = new DateTimeOffset(targetDate.Year, targetDate.Month, targetDate.Day, 0, 0, 0, TimeSpan.Zero);
        var targetDayStartUnix = targetDayStart.ToUnixTimeSeconds();

        // Look for the exact daily candle for this day (timestamp at 00:00:00 of the same day)
        var exactDayIndex = -1;
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i] == targetDayStartUnix)
            {
                exactDayIndex = i;
                break;
            }
        }

        if (exactDayIndex >= 0)
        {
            // Found exact daily candle for this day
            bestCandle = rates.Values[exactDayIndex];
        }
        else if (bestBefore >= 0)
        {
            // Use the most recent daily candle before this day
            bestCandle = rates.Values[bestBefore];
        }
        else if (keys.Count > 0)
        {
            // Use the earliest available candle if no historical data
            bestCandle = rates.Values[0];
        }

        return bestCandle?.GetRate(rateType) ?? 0m;
    }

    /// <summary>
    /// Gets information about the rate used for a specific asset at a given time.
    /// Returns a formatted string showing the source timestamp of the rate data.
    /// </summary>
    public string? GetRateInfo(string fromCurrency, DateTimeOffset date)
    {
        fromCurrency = KrakenLedgerEntry.NormaliseAssetName(fromCurrency).ToUpperInvariant();

        if (fromCurrency == "GBP") return null;

        var timestamp = date.ToUnixTimeSeconds();

        // Try direct GBP conversion first
        var rate = TryGetRateInfo(fromCurrency, "GBP", timestamp);
        if (rate != null) return rate;

        // Try via USD
        var usdRate = TryGetRateInfo(fromCurrency, "USD", timestamp);
        if (usdRate != null) return usdRate;

        // Try CSV fallback
        if (_delistedPrices != null)
        {
            var csvCandle = _delistedPrices.TryGetRate(fromCurrency, "GBP", timestamp)
                         ?? _delistedPrices.TryGetRate(fromCurrency, "USD", timestamp)
                         ?? _delistedPrices.TryGetRate(fromCurrency, "EUR", timestamp);
            if (csvCandle != null)
            {
                var rateDate = DateTimeOffset.FromUnixTimeSeconds(csvCandle.Timestamp);
                return $"{rateDate:dd/MM} (Kraken CSV)";
            }
        }

        // Manual pair override
        if (TryGetManualRate(fromCurrency, timestamp).HasValue)
            return "Manual Override";

        return null;
    }

    private string? TryGetRateInfo(string from, string to, long timestamp)
    {
        from = from.ToUpperInvariant();
        to = to.ToUpperInvariant();

        if (!_pairMap.TryGetValue((from, to), out var pair))
            return null;

        if (!_rateCache.TryGetValue(pair.CacheKey, out var rates) || rates.Count == 0)
            return null;

        var keys = rates.Keys;
        int lo = 0, hi = keys.Count - 1;
        int bestBefore = -1;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] <= timestamp)
            {
                bestBefore = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        long? rateTimestamp = null;

        // For daily candles, find the same-day candle or most recent historical candle
        var targetDate = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        var targetDayStart = new DateTimeOffset(targetDate.Year, targetDate.Month, targetDate.Day, 0, 0, 0, TimeSpan.Zero);
        var targetDayStartUnix = targetDayStart.ToUnixTimeSeconds();

        // Look for the exact daily candle for this day
        var exactDayIndex = -1;
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i] == targetDayStartUnix)
            {
                exactDayIndex = i;
                break;
            }
        }

        if (exactDayIndex >= 0)
        {
            rateTimestamp = keys[exactDayIndex];
        }
        else if (bestBefore >= 0)
        {
            rateTimestamp = keys[bestBefore];
        }
        else if (keys.Count > 0)
        {
            rateTimestamp = keys[0];
        }

        if (rateTimestamp.HasValue)
        {
            var rateDate = DateTimeOffset.FromUnixTimeSeconds(rateTimestamp.Value);
            var source = _pairSources.GetValueOrDefault(pair.CacheKey, "Unknown");

            // Format description based on the rate type for clarity
            var description = _rateType switch
            {
                FxRateType.Open => $"{rateDate:dd/MM} daily open ({source})",
                FxRateType.High => $"{rateDate:dd/MM} daily high ({source})",
                FxRateType.Low => $"{rateDate:dd/MM} daily low ({source})",
                FxRateType.Close => $"{rateDate:dd/MM} end of day ({source})",
                FxRateType.Average => $"{rateDate:dd/MM} daily average ({source})",
                _ => $"{rateDate:dd/MM} ({source})"
            };

            return description;
        }

        return null;
    }

    private static decimal FindClosestManualRate(SortedList<long, decimal> rates, long targetTimestamp)
    {
        var keys = rates.Keys;
        int lo = 0, hi = keys.Count - 1;
        int bestBefore = -1;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] <= targetTimestamp)
            {
                bestBefore = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Exact match
        if (bestBefore >= 0 && keys[bestBefore] == targetTimestamp)
            return rates.Values[bestBefore];

        // Prefer next available rate
        if (bestBefore + 1 < keys.Count)
            return rates.Values[bestBefore + 1];

        // No later rate — fall back to most recent previous
        if (bestBefore >= 0)
            return rates.Values[bestBefore];

        return 0m;
    }

    /// <summary>
    /// Ensures rate data for a cache key is loaded. Returns true if a network download was made.
    /// Only downloads if the cached data does not already cover up to latestNeeded.
    /// </summary>
    private async Task<bool> EnsurePairLoadedAsync(
        string cacheKey, DateTimeOffset earliest, DateTimeOffset latestNeeded,
        CancellationToken ct, bool cacheOnly = false)
    {
        // Load from memory or disk if not already present
        if (!_rateCache.ContainsKey(cacheKey) || _rateCache[cacheKey].Count == 0)
            TryLoadFromCache(cacheKey);

        if (_rateCache.TryGetValue(cacheKey, out var rates) && rates.Count > 0)
        {
            if (cacheOnly) return false;

            var earliestCached = DateTimeOffset.FromUnixTimeSeconds(rates.Keys.First());
            var latestCached = DateTimeOffset.FromUnixTimeSeconds(rates.Keys.Last());

            // Check both directions: does the cache cover back to 'earliest' AND forward to 'latestNeeded'?
            // 7-day buffer handles weekends/holidays and short data gaps.
            bool needsHistoricalBackfill = earliestCached > earliest.AddDays(7);
            bool needsForwardExtension = latestCached < latestNeeded.AddDays(-7);

            if (!needsHistoricalBackfill && !needsForwardExtension)
                return false; // Already sufficient — no download needed

            if (!cacheKey.StartsWith("CC_", StringComparison.OrdinalIgnoreCase))
            {
                if (needsHistoricalBackfill)
                    await DownloadAndPrependAsync(cacheKey, earliest, rates, ct);
                else if (needsForwardExtension)
                    await DownloadAndAppendAsync(cacheKey, rates.Keys.Last(), ct);
                return true;
            }

            // CryptoCompare pair is stale — do a full re-download (no append API available)
            var ccParts = cacheKey.Split('_');
            if (ccParts.Length == 3)
            {
                // If backfill needed, use the earlier start date so re-download covers the full period
                var ccEarliest = needsHistoricalBackfill ? earliest : earliestCached;
                if (await TryDownloadCryptoCompareAsync(ccParts[1], ccParts[2], cacheKey, ccEarliest, ct, forceRefresh: true))
                    return true;
            }
            return false;
        }

        if (cacheOnly) return false;

        // No cache at all — full download
        if (!cacheKey.StartsWith("CC_", StringComparison.OrdinalIgnoreCase))
        {
            var ok = await TryDownloadKrakenPairAsync(cacheKey, earliest, ct);
            return ok;
        }

        // CC pair with no cache — try to download it directly
        var parts = cacheKey.Split('_');
        if (parts.Length == 3)
            return await TryDownloadCryptoCompareAsync(parts[1], parts[2], cacheKey, earliest, ct);

        return false;
    }

    private async Task DownloadAndAppendAsync(string krakenPair, long sinceTimestamp, CancellationToken ct)
    {
        try
        {
            var candles = await _krakenApi.GetOhlcDataAsync(krakenPair, sinceTimestamp, ct);
            if (candles.Count > 0 && _rateCache.TryGetValue(krakenPair, out var existing))
            {
                foreach (var candle in candles)
                    existing[candle.Timestamp] = candle;
                SaveToCache(krakenPair, existing);
            }
        }
        catch
        {
            // Non-critical — we already have cached data
        }
    }

    /// <summary>
    /// Downloads daily candles from Kraken starting from <paramref name="earliest"/> and merges
    /// ALL returned candles into the cache regardless of existing date coverage.
    /// One API call yields up to 720 daily candles (~2 years), so a single request covers
    /// both the historical gap and any forward extension simultaneously.
    /// </summary>
    private async Task DownloadAndPrependAsync(
        string krakenPair, DateTimeOffset earliest,
        SortedList<long, OhlcCandle> rates, CancellationToken ct)
    {
        try
        {
            var currentSince = earliest.AddDays(-7).ToUnixTimeSeconds();
            bool updated = false;

            while (!ct.IsCancellationRequested)
            {
                var candles = await _krakenApi.GetOhlcDataAsync(krakenPair, currentSince, ct);
                if (candles.Count == 0) break;

                foreach (var c in candles)
                    rates[c.Timestamp] = c;
                updated = true;

                if (candles.Count < 720)
                    break;

                currentSince = candles.Last().Timestamp;
                await Task.Delay(1500, ct);
            }

            if (updated)
                SaveToCache(krakenPair, rates);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Non-critical — we already have cached data for the recent period
        }
    }

    private bool TryLoadFromCache(string cacheKey)
    {
        var cachePath = Path.Combine(_cacheFolder, $"{cacheKey}.json");
        if (!File.Exists(cachePath)) return false;

        try
        {
            var json = File.ReadAllText(cachePath);

            // Try to deserialize as new OHLC format first
            try
            {
                var ohlcEntries = JsonSerializer.Deserialize<Dictionary<string, OhlcCandle>>(json);
                if (ohlcEntries != null && ohlcEntries.Count > 0)
                {
                    var parsed = new List<KeyValuePair<long, OhlcCandle>>(ohlcEntries.Count);
                    foreach (var (key, candle) in ohlcEntries)
                    {
                        if (long.TryParse(key, out var ts))
                        {
                            candle.Timestamp = ts; // Ensure timestamp is set
                            parsed.Add(new(ts, candle));
                        }
                        else if (DateOnly.TryParse(key, out var date))
                        {
                            var dto = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                            var timestamp = dto.ToUnixTimeSeconds();
                            candle.Timestamp = timestamp;
                            parsed.Add(new(timestamp, candle));
                        }
                    }

                    if (parsed.Count > 0)
                    {
                        parsed.Sort((a, b) => a.Key.CompareTo(b.Key));
                        var rates = new SortedList<long, OhlcCandle>(parsed.Count);
                        foreach (var kv in parsed)
                            rates[kv.Key] = kv.Value;

                        _rateCache[cacheKey] = rates;

                        if (!_pairSources.ContainsKey(cacheKey))
                            _pairSources[cacheKey] = cacheKey.StartsWith("CC_", StringComparison.OrdinalIgnoreCase)
                                ? "CryptoCompare" : "Kraken";

                        return true;
                    }
                }
            }
            catch
            {
                // Not OHLC format, try legacy decimal format
            }

            // Fallback: try legacy decimal format for backward compatibility
            var entries = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json);
            if (entries == null || entries.Count == 0) return false;

            var parsedLegacy = new List<KeyValuePair<long, decimal>>(entries.Count);
            foreach (var (key, rate) in entries)
            {
                if (long.TryParse(key, out var ts))
                {
                    parsedLegacy.Add(new(ts, rate));
                }
                else if (DateOnly.TryParse(key, out var date))
                {
                    var dto = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    parsedLegacy.Add(new(dto.ToUnixTimeSeconds(), rate));
                }
            }

            if (parsedLegacy.Count == 0) return false;

            parsedLegacy.Sort((a, b) => a.Key.CompareTo(b.Key));

            // Convert legacy decimal rates to OHLC candles (all OHLC values = close price)
            var ratesFromLegacy = new SortedList<long, OhlcCandle>(parsedLegacy.Count);
            foreach (var kv in parsedLegacy)
            {
                ratesFromLegacy[kv.Key] = new OhlcCandle
                {
                    Timestamp = kv.Key,
                    Open = kv.Value,
                    High = kv.Value,
                    Low = kv.Value,
                    Close = kv.Value
                };
            }

            _rateCache[cacheKey] = ratesFromLegacy;

            // Infer source from cache key naming
            if (!_pairSources.ContainsKey(cacheKey))
                _pairSources[cacheKey] = cacheKey.StartsWith("CC_", StringComparison.OrdinalIgnoreCase)
                    ? "CryptoCompare" : "Kraken";

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveToCache(string cacheKey, SortedList<long, OhlcCandle> rates)
    {
        try
        {
            Directory.CreateDirectory(_cacheFolder);
            var dict = rates.ToDictionary(r => r.Key.ToString(), r => r.Value);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
            var path = Path.Combine(_cacheFolder, $"{cacheKey}.json");
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Warning,
                Category = "FX Cache",
                Message = $"Failed to save FX cache for {cacheKey}: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Returns information about all cached FX rate pairs.
    /// </summary>
    public List<FxCacheInfo> GetCacheStats()
    {
        var results = new List<FxCacheInfo>();

        foreach (var (pair, rates) in _rateCache)
        {
            if (rates.Count == 0) continue;
            if (_activeCacheKeys.Count > 0 && !_activeCacheKeys.Contains(pair)) continue;

            _pairSources.TryGetValue(pair, out var source);

            results.Add(new FxCacheInfo
            {
                PairName = pair,
                DataPoints = rates.Count,
                EarliestDate = DateTimeOffset.FromUnixTimeSeconds(rates.Keys.First()),
                LatestDate = DateTimeOffset.FromUnixTimeSeconds(rates.Keys.Last()),
                SampleRate = rates.Values.Last().Close,
                OnDisk = File.Exists(Path.Combine(_cacheFolder, $"{pair}.json")),
                DataSource = source ?? "Unknown"
            });
        }

        // Also surface pairs from the CSV dataset that aren't in the live cache
        if (_delistedPrices != null)
        {
            foreach (var pairName in _delistedPrices.GetPairNames())
            {
                if (_rateCache.ContainsKey(pairName)) continue;
                if (_activeCsvPairNames.Count > 0 && !_activeCsvPairNames.Contains(pairName)) continue;
                var candles = _delistedPrices.GetPairCandles(pairName);
                if (candles == null || candles.Count == 0) continue;

                results.Add(new FxCacheInfo
                {
                    PairName = pairName,
                    DataPoints = candles.Count,
                    EarliestDate = DateTimeOffset.FromUnixTimeSeconds(candles.Keys.First()),
                    LatestDate = DateTimeOffset.FromUnixTimeSeconds(candles.Keys.Last()),
                    SampleRate = candles.Values[candles.Count - 1].Close,
                    OnDisk = false,
                    DataSource = "Kraken (CSV)"
                });
            }
        }

        return results.OrderBy(r => r.PairName).ToList();
    }

    public string CacheFolderPath => _cacheFolder;

    /// <summary>
    /// Returns all individual rate data points for a given pair, ordered by date.
    /// Returns empty list if pair not found.
    /// </summary>
    public List<(DateTimeOffset Date, decimal Open, decimal High, decimal Low, decimal Close, decimal Average, string Source)> GetRateDataPoints(string pairName)
    {
        // Handle manual override entries selected via "[Manual] ASSET" naming
        const string manualPrefix = "[Manual] ";
        if (pairName.StartsWith(manualPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var asset = pairName[manualPrefix.Length..].ToUpperInvariant();
            if (_manualOverrides.TryGetValue(asset, out var manualRates))
                return manualRates.Select(kv =>
                {
                    var r = kv.Value;
                    return (Date: DateTimeOffset.FromUnixTimeSeconds(kv.Key),
                            Open: r, High: r, Low: r, Close: r, Average: r,
                            Source: "Manual Override");
                }).ToList();
            return new();
        }

        var result = new List<(DateTimeOffset Date, decimal Open, decimal High, decimal Low, decimal Close, decimal Average, string Source)>();

        // Kraken live / CryptoCompare cache
        if (_rateCache.TryGetValue(pairName, out var rates))
        {
            _pairSources.TryGetValue(pairName, out var src);
            result.AddRange(rates.Select(kv => (
                Date:    DateTimeOffset.FromUnixTimeSeconds(kv.Key),
                Open:    kv.Value.Open,
                High:    kv.Value.High,
                Low:     kv.Value.Low,
                Close:   kv.Value.Close,
                Average: (kv.Value.High + kv.Value.Low) / 2m,
                Source:  src ?? "Kraken"
            )));
        }
        else
        {
            // Fall back to CSV data for delisted pairs
            var csvCandles = _delistedPrices?.GetPairCandles(pairName);
            if (csvCandles != null)
                result.AddRange(csvCandles.Select(kv => (
                    Date:    DateTimeOffset.FromUnixTimeSeconds(kv.Key),
                    Open:    kv.Value.Open,
                    High:    kv.Value.High,
                    Low:     kv.Value.Low,
                    Close:   kv.Value.Close,
                    Average: (kv.Value.High + kv.Value.Low) / 2m,
                    Source:  "Kraken (CSV)"
                )));
        }

        // Merge manual overrides for this pair inline — shown with their own Source label
        if (_manualOverrides.TryGetValue(pairName, out var manualForPair))
            result.AddRange(manualForPair.Select(kv =>
            {
                var r = kv.Value;
                return (Date: DateTimeOffset.FromUnixTimeSeconds(kv.Key),
                        Open: r, High: r, Low: r, Close: r, Average: r,
                        Source: "Manual Override");
            }));

        return result.OrderBy(p => p.Date).ToList();
    }

    /// <summary>
    /// Returns all pair names in the cache.
    /// </summary>
    public List<string> GetPairNames()
    {
        var names = new HashSet<string>(
            _rateCache.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);

        if (_delistedPrices != null)
            foreach (var p in _delistedPrices.GetPairNames())
                names.Add(p);

        // Only add [Manual] prefix for pairs with no cached/CSV data — for known pairs the
        // manual override points are merged inline by GetRateDataPoints.
        var result = new List<string>(names.Count + _manualOverrides.Count);
        foreach (var asset in _manualOverrides.Keys.OrderBy(a => a))
            if (!names.Contains(asset))
                result.Add($"[Manual] {asset}");

        result.AddRange(names.OrderBy(k => k));

        return result;
    }

    /// <summary>
    /// Adds a manual GBP rate for an asset at a specific date. Persisted immediately.
    /// Multiple entries per asset are stored and interpolated using FindClosestRate.
    /// </summary>
    public void SetManualOverride(string asset, DateTimeOffset date, decimal gbpRate)
    {
        asset = asset.Trim().ToUpperInvariant();
        if (!_manualOverrides.ContainsKey(asset))
            _manualOverrides[asset] = new SortedList<long, decimal>();
        _manualOverrides[asset][date.ToUnixTimeSeconds()] = gbpRate;
        SaveManualOverrides();
    }

    /// <summary>
    /// Removes a specific dated entry for an asset. Persisted immediately.
    /// </summary>
    public void RemoveManualOverride(string asset, DateTimeOffset date)
    {
        asset = asset.Trim().ToUpperInvariant();
        if (_manualOverrides.TryGetValue(asset, out var rates))
        {
            rates.Remove(date.ToUnixTimeSeconds());
            if (rates.Count == 0)
                _manualOverrides.Remove(asset);
        }
        SaveManualOverrides();
    }

    /// <summary>
    /// Returns all manual override entries as a flat list sorted by asset then date.
    /// </summary>
    public List<(string Asset, DateTimeOffset Date, decimal Rate)> GetManualOverrides()
        => _manualOverrides
            .SelectMany(kv => kv.Value.Select(r =>
                (kv.Key, DateTimeOffset.FromUnixTimeSeconds(r.Key), r.Value)))
            .OrderBy(x => x.Key).ThenBy(x => x.Item2)
            .ToList();

    /// <summary>
    /// Returns all discovered Kraken trading pairs for debugging/monitoring
    /// </summary>
    public List<(string Asset, string Quote, string KrakenPair, bool Invert)> GetDiscoveredKrakenPairs()
        => _krakenDiscoveredPairs
            .Select(kv => (kv.Key.Asset, kv.Key.Quote, kv.Value.KrakenPair, kv.Value.Invert))
            .OrderBy(x => x.Asset).ThenBy(x => x.Quote)
            .ToList();

    /// <summary>
    /// Forces a refresh of Kraken trading pairs, bypassing the 24-hour cache
    /// </summary>
    public async Task ForceRefreshKrakenPairsAsync(CancellationToken ct = default)
    {
        await RefreshKrakenPairsAsync(ct, force: true);
    }

    private void LoadManualOverrides()
    {
        if (!File.Exists(_manualOverridesFile)) return;
        try
        {
            var json = File.ReadAllText(_manualOverridesFile);
            // Format: { "BTC": { "1609459200": 25000.0, ... }, ... }
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, decimal>>>(json);
            if (loaded == null) return;
            foreach (var (asset, entries) in loaded)
            {
                var key = asset.ToUpperInvariant();
                if (!_manualOverrides.ContainsKey(key))
                    _manualOverrides[key] = new SortedList<long, decimal>();
                foreach (var (tsStr, rate) in entries)
                    if (long.TryParse(tsStr, out var ts))
                        _manualOverrides[key][ts] = rate;
            }
        }
        catch { }
    }

    private void SaveManualOverrides()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_manualOverridesFile)!);
            // Serialise as { "BTC": { "1609459200": 25000.0 } }
            var dict = _manualOverrides.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(r => r.Key.ToString(), r => r.Value));
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_manualOverridesFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Scans the cache folder and loads every rate file into memory.
    /// Also reconstructs _pairMap entries so TryGetRate can find them.
    /// </summary>
    public void LoadAllFromDiskCache()
    {
        if (!Directory.Exists(_cacheFolder)) return;

        foreach (var file in Directory.GetFiles(_cacheFolder, "*.json"))
        {
            var cacheKey = Path.GetFileNameWithoutExtension(file);

            // Skip metadata files
            if (cacheKey.Equals("manual_overrides", StringComparison.OrdinalIgnoreCase)) continue;
            if (cacheKey.Equals("pairmap", StringComparison.OrdinalIgnoreCase)) continue;
            if (cacheKey.Equals("kraken_pairs", StringComparison.OrdinalIgnoreCase)) continue;

            TryLoadFromCache(cacheKey);

            // Ensure _pairMap has an entry so TryGetRate can look up this cache key.
            if (cacheKey.StartsWith("CC_", StringComparison.OrdinalIgnoreCase))
            {
                // CC_ASSET_QUOTE — asset and quote are encoded in the key
                var parts = cacheKey.Split('_');
                if (parts.Length == 3)
                {
                    var mapKey = (parts[1].ToUpperInvariant(), parts[2].ToUpperInvariant());
                    if (!_pairMap.ContainsKey(mapKey))
                        _pairMap[mapKey] = (cacheKey, false);
                }
            }
            else
            {
                // Try to find this Kraken pair in our discovered pairs
                foreach (var kv in _krakenDiscoveredPairs)
                {
                    if (kv.Value.KrakenPair.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_pairMap.ContainsKey(kv.Key))
                            _pairMap[kv.Key] = kv.Value;
                        break;
                    }
                }

                // If not found in discovered pairs, this might be an old cached pair
                // We'll let the dynamic discovery handle it on the next run
            }
        }
    }

    /// <summary>
    /// Persists the full _pairMap to disk so dynamically discovered pairs
    /// (CryptoCompare fallbacks, alias discoveries) survive app restarts.
    /// </summary>
    private void SavePairMap()
    {
        try
        {
            var dict = _pairMap.ToDictionary(
                kv => $"{kv.Key.Asset}|{kv.Key.Quote}",
                kv => new { cacheKey = kv.Value.CacheKey, invert = kv.Value.Invert });
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_pairMapFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Restores dynamically discovered pair mappings from the previous session's pairmap.json.
    /// KnownPairs entries are already in _pairMap from the constructor, so they won't be overwritten.
    /// </summary>
    private void LoadPairMap()
    {
        if (!File.Exists(_pairMapFile)) return;
        try
        {
            var json = File.ReadAllText(_pairMapFile);
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var parts = prop.Name.Split('|');
                if (parts.Length != 2) continue;
                var mapKey = (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());

                var cacheKey = prop.Value.GetProperty("cacheKey").GetString() ?? "";
                var invert = prop.Value.GetProperty("invert").GetBoolean();
                if (!string.IsNullOrEmpty(cacheKey))
                    // pairmap.json always wins over KnownPairs defaults — it reflects what
                    // actually worked last session (e.g. an asset that fell back from Kraken
                    // to CryptoCompare because the Kraken pair returned no data).
                    _pairMap[mapKey] = (cacheKey, invert);
            }
        }
        catch { }
    }
}

public class FxCacheInfo
{
    public string PairName { get; set; } = "";
    public int DataPoints { get; set; }
    public DateTimeOffset EarliestDate { get; set; }
    public DateTimeOffset LatestDate { get; set; }
    public decimal SampleRate { get; set; }
    public bool OnDisk { get; set; }
    public string DataSource { get; set; } = "";
}
