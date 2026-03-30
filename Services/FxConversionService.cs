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
/// Primary source: Kraken public OHLC API (4-hour candles).
/// Fallback source: CryptoCompare (hourly candles) for delisted/missing pairs.
/// Rates are cached permanently on disk — historical data never expires.
///
/// IMPORTANT: USDT is NOT USD. USDT → USD → GBP (two-step conversion).
/// </summary>
public class FxConversionService
{
    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoTax2026", "fx_cache");

    private static readonly string ManualOverridesFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoTax2026", "fx_cache", "manual_overrides.json");

    private static readonly string PairMapFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoTax2026", "fx_cache", "pairmap.json");

    private readonly KrakenApiService _krakenApi;
    private readonly List<CalculationWarning> _warnings;
    private readonly HttpClient _fallbackHttp;

    // Cache: key = pair/source key, value = sorted list of (unix_timestamp -> close_price)
    private readonly Dictionary<string, SortedList<long, decimal>> _rateCache = new(StringComparer.OrdinalIgnoreCase);

    // Runtime-discovered pair mappings: (asset, quoteCurrency) -> (cacheKey, invert)
    private readonly Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)> _pairMap = new();

    // Track data source per cache key
    private readonly Dictionary<string, string> _pairSources = new(StringComparer.OrdinalIgnoreCase);

    // Known Kraken OHLC API pair names for common conversions.
    // IMPORTANT: Kraken's OHLC API pair names differ from trade/ledger pair names.
    // e.g. OHLC uses "XRPGBP" but trades use "XXRPZGBP". Always verify with the OHLC endpoint.
    private static readonly (string From, string To, string KrakenPair, bool Invert)[] KnownPairs = new[]
    {
        // Fiat to GBP (verified against Kraken OHLC API)
        ("USD", "GBP", "GBPUSD", true),      // Kraken has GBP/USD → invert for USD→GBP
        ("EUR", "GBP", "EURGBP", false),      // Kraken has EUR/GBP directly (NOT GBPEUR)

        // Stablecoins to USD
        ("USDT", "USD", "USDTUSD", false),
        ("USDC", "USD", "USDCUSD", false),
        ("DAI", "USD", "DAIUSD", false),

        // Crypto to GBP — verified pair names for Kraken OHLC API
        ("BTC", "GBP", "XXBTZGBP", false),    // BTC still uses legacy format
        ("ETH", "GBP", "XETHZGBP", false),    // ETH still uses legacy format
        ("XRP", "GBP", "XRPGBP", false),      // NOT XXRPZGBP — that fails on OHLC
        ("LTC", "GBP", "LTCGBP", false),      // NOT XLTCZGBP — that fails on OHLC
        ("ADA", "GBP", "ADAGBP", false),
        ("DOT", "GBP", "DOTGBP", false),
        ("SOL", "GBP", "SOLGBP", false),
        ("DOGE", "GBP", "XDGGBP", false),
        ("LINK", "GBP", "LINKGBP", false),
        ("POL", "GBP", "POLGBP", false),      // No GBP pair on Kraken — will fallback to USD
        ("AVAX", "GBP", "AVAXGBP", false),
        ("ATOM", "GBP", "ATOMGBP", false),
        ("ALGO", "GBP", "ALGOGBP", false),
        ("TIA", "GBP", "TIAGBP", false),
        ("NEAR", "GBP", "NEARGBP", false),
        ("FIL", "GBP", "FILGBP", false),
        ("APT", "GBP", "APTGBP", false),
        ("ARB", "GBP", "ARBGBP", false),
        ("OP", "GBP", "OPGBP", false),
        ("INJ", "GBP", "INJGBP", false),
        ("MANA", "GBP", "MANAGBP", false),
        ("SAND", "GBP", "SANDGBP", false),
        ("GRT", "GBP", "GRTGBP", false),
        ("AAVE", "GBP", "AAVEGBP", false),
        ("SNX", "GBP", "SNXGBP", false),
        ("CRV", "GBP", "CRVGBP", false),
        ("UNI", "GBP", "UNIGBP", false),
        ("COMP", "GBP", "COMPGBP", false),
        ("MKR", "GBP", "MKRGBP", false),
        ("SUSHI", "GBP", "SUSHIGBP", false),
        ("YFI", "GBP", "YFIGBP", false),
        ("BAT", "GBP", "BATGBP", false),
        ("ZRX", "GBP", "ZRXGBP", false),
        ("ENJ", "GBP", "ENJGBP", false),
        ("CHZ", "GBP", "CHZGBP", false),
        ("FLOW", "GBP", "FLOWGBP", false),
        ("KSM", "GBP", "KSMGBP", false),
        ("MINA", "GBP", "MINAGBP", false),
        ("KAVA", "GBP", "KAVAGBP", false),
        ("FET", "GBP", "FETGBP", false),
        ("OCEAN", "GBP", "OCEANGBP", false),
        ("RUNE", "GBP", "RUNEGBP", false),
        ("SCRT", "GBP", "SCRTGBP", false),
        ("SUI", "GBP", "SUIGBP", false),
        ("SEI", "GBP", "SEIGBP", false),
        ("PEPE", "GBP", "PEPEGBP", false),
        ("WIF", "GBP", "WIFGBP", false),
        ("RENDER", "GBP", "RENDERGBP", false),
        ("JUP", "GBP", "JUPGBP", false),
        ("PYTH", "GBP", "PYTHGBP", false),
        ("BONK", "GBP", "BONKGBP", false),
        ("STRK", "GBP", "STRKGBP", false),
        ("PENDLE", "GBP", "PENDLEGBP", false),
        ("ETHFI", "GBP", "ETHFIGBP", false),

        // Crypto to USD — fallback when no GBP pair exists
        ("BTC", "USD", "XXBTZUSD", false),
        ("ETH", "USD", "XETHZUSD", false),
        ("XRP", "USD", "XRPUSD", false),      // NOT XXRPZUSD
        ("LTC", "USD", "LTCUSD", false),       // NOT XLTCZUSD
        ("ADA", "USD", "ADAUSD", false),
        ("DOT", "USD", "DOTUSD", false),
        ("SOL", "USD", "SOLUSD", false),
        ("DOGE", "USD", "XDGUSD", false),
        ("LINK", "USD", "LINKUSD", false),
        ("POL", "USD", "POLUSD", false),
        ("AVAX", "USD", "AVAXUSD", false),
        ("ATOM", "USD", "ATOMUSD", false),
        ("ALGO", "USD", "ALGOUSD", false),
        ("TIA", "USD", "TIAUSD", false),
        ("NEAR", "USD", "NEARUSD", false),
        ("FIL", "USD", "FILUSD", false),
        ("APT", "USD", "APTUSD", false),
        ("ARB", "USD", "ARBUSD", false),
        ("OP", "USD", "OPUSD", false),
        ("INJ", "USD", "INJUSD", false),
        ("MANA", "USD", "MANAUSD", false),
        ("SAND", "USD", "SANDUSD", false),
        ("GRT", "USD", "GRTUSD", false),
        ("AAVE", "USD", "AAVEUSD", false),
        ("SNX", "USD", "SNXUSD", false),
        ("CRV", "USD", "CRVUSD", false),
        ("UNI", "USD", "UNIUSD", false),
        ("COMP", "USD", "COMPUSD", false),
        ("MKR", "USD", "MKRUSD", false),
        ("SUSHI", "USD", "SUSHIUSD", false),
        ("YFI", "USD", "YFIUSD", false),
        ("BAT", "USD", "BATUSD", false),
        ("ZRX", "USD", "ZRXUSD", false),
        ("ENJ", "USD", "ENJUSD", false),
        ("CHZ", "USD", "CHZUSD", false),
        ("FLOW", "USD", "FLOWUSD", false),
        ("KSM", "USD", "KSMUSD", false),
        ("MINA", "USD", "MINAUSD", false),
        ("KAVA", "USD", "KAVAUSD", false),
        ("FET", "USD", "FETUSD", false),
        ("OCEAN", "USD", "OCEANUSD", false),
        ("RUNE", "USD", "RUNEUSD", false),
        ("SCRT", "USD", "SCRTUSD", false),
        ("TAO", "USD", "TAOUSD", false),
        ("SUI", "USD", "SUIUSD", false),
        ("SEI", "USD", "SEIUSD", false),
        ("PEPE", "USD", "PEPEUSD", false),
        ("WIF", "USD", "WIFUSD", false),
        ("RENDER", "USD", "RENDERUSD", false),
        ("JUP", "USD", "JUPUSD", false),
        ("PYTH", "USD", "PYTHUSD", false),
        ("BONK", "USD", "BONKUSD", false),
        ("STRK", "USD", "STRKUSD", false),
        ("PENDLE", "USD", "PENDLEUSD", false),
        ("ETHFI", "USD", "ETHFIUSD", false),
    };

    // Reverse lookup: Kraken pair name → (asset, quote, invert) for reconstructing _pairMap on load
    private static readonly Dictionary<string, (string Asset, string Quote, bool Invert)> KnownPairByCacheKey =
        KnownPairs
            .GroupBy(p => p.KrakenPair, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (g.First().From.ToUpperInvariant(), g.First().To.ToUpperInvariant(), g.First().Invert),
                StringComparer.OrdinalIgnoreCase);

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

    public FxConversionService(KrakenApiService krakenApi, List<CalculationWarning> warnings)
    {
        _krakenApi = krakenApi;
        _warnings = warnings;
        Directory.CreateDirectory(CacheFolder);

        _fallbackHttp = new HttpClient();
        LoadManualOverrides();
        _fallbackHttp.DefaultRequestHeaders.Add("User-Agent", "CryptoTax2026/1.0");

        // Populate initial pair map from known pairs
        // Don't overwrite if already set (first entry wins for each (From,To) key)
        foreach (var p in KnownPairs)
        {
            var key = (p.From.ToUpperInvariant(), p.To.ToUpperInvariant());
            if (!_pairMap.ContainsKey(key))
                _pairMap[key] = (p.KrakenPair, p.Invert);
        }

        // Restore dynamically discovered pairs from previous sessions
        LoadPairMap();
    }

    /// <summary>
    /// Pre-loads all FX rate data needed for the given set of currencies and date range.
    /// Tries Kraken first, then aliases, then CryptoCompare as fallback.
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
                // For crypto: add GBP pair if known, otherwise add USD pair
                if (_pairMap.TryGetValue((currency, "GBP"), out var gbpPair))
                {
                    pairsToDownload.Add(gbpPair.CacheKey);
                }
                else if (_pairMap.TryGetValue((currency, "USD"), out var usdPair))
                {
                    pairsToDownload.Add(usdPair.CacheKey);
                    pairsToDownload.Add("GBPUSD");
                }
                // else: unknown — will try dynamic discovery + fallback below
            }
        }

        // Download known pairs first
        int loaded = 0;
        int totalPairs = pairsToDownload.Count;
        foreach (var pair in pairsToDownload.ToList())
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
            // Dynamic discovery for currencies we don't have rates for yet
            var undiscoveredCurrencies = neededCurrencies
                .Where(c => c is not "USD" and not "EUR" and not "USDT" and not "USDC" and not "DAI")
                .Where(c => !_pairMap.ContainsKey((c, "GBP")) && !_pairMap.ContainsKey((c, "USD")))
                .ToList();

            foreach (var currency in undiscoveredCurrencies)
            {
                if (ct.IsCancellationRequested) break;
                progress?.Report((loaded, $"Discovering FX pair for {currency}..."));

                var discovered = await TryDiscoverPairAsync(currency, earliest, ct);
                if (discovered)
                {
                    loaded++;
                    continue;
                }

                // Try aliases on Kraken (e.g. POL → try MATIC pairs)
                if (CoinAliases.TryGetValue(currency, out var aliases))
                {
                    foreach (var alias in aliases)
                    {
                        if (ct.IsCancellationRequested) break;
                        progress?.Report((loaded, $"Trying alias {alias} for {currency}..."));

                        var aliasFound = await TryDiscoverPairForAliasAsync(currency, alias, earliest, ct);
                        if (aliasFound)
                        {
                            loaded++;
                            break;
                        }
                    }

                    // Check if we found it via alias
                    if (_pairMap.ContainsKey((currency, "GBP")) || _pairMap.ContainsKey((currency, "USD")))
                        continue;
                }

                // Fallback: CryptoCompare
                progress?.Report((loaded, $"Trying CryptoCompare for {currency}..."));
                var fallbackOk = await TryFallbackCryptoCompareAsync(currency, earliest, ct);
                if (fallbackOk)
                    loaded++;

                await Task.Delay(500, ct);
            }

            // Also try CryptoCompare for any known-pair currencies that failed to load from Kraken
            var failedKnownCurrencies = neededCurrencies
                .Where(c => c is not "USD" and not "EUR" and not "USDT" and not "USDC" and not "DAI")
                .Where(c =>
                {
                    // Has a pair mapping but no actual rate data
                    if (_pairMap.TryGetValue((c, "GBP"), out var gp))
                        if (_rateCache.ContainsKey(gp.CacheKey) && _rateCache[gp.CacheKey].Count > 0) return false;
                    if (_pairMap.TryGetValue((c, "USD"), out var up))
                        if (_rateCache.ContainsKey(up.CacheKey) && _rateCache[up.CacheKey].Count > 0) return false;
                    // Already handled by discovery above
                    if (undiscoveredCurrencies.Contains(c)) return false;
                    return true;
                })
                .ToList();

            foreach (var currency in failedKnownCurrencies)
            {
                if (ct.IsCancellationRequested) break;
                progress?.Report((loaded, $"Trying CryptoCompare fallback for {currency}..."));
                var fallbackOk = await TryFallbackCryptoCompareAsync(currency, earliest, ct);
                if (fallbackOk) loaded++;
                await Task.Delay(500, ct);
            }
        }

        progress?.Report((loaded, $"FX rates loaded for {loaded} pairs."));

        // Persist the full pair map so dynamic discoveries survive app restarts
        if (!cacheOnly)
            SavePairMap();
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
    /// Fallback: downloads hourly rate data from CryptoCompare for assets Kraken doesn't have.
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
    /// Downloads hourly OHLC data from CryptoCompare.
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

        var allRates = new SortedList<long, decimal>();
        var toTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sinceTs = earliest.AddDays(-7).ToUnixTimeSeconds();
        int maxSegments = 20; // safety limit

        try
        {
            for (int seg = 0; seg < maxSegments && !ct.IsCancellationRequested; seg++)
            {
                // CryptoCompare histohour: max 2000 data points per request
                var url = $"https://min-api.cryptocompare.com/data/v2/histohour" +
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
                        allRates[time] = close;
                        added++;
                        if (time < earliestInBatch) earliestInBatch = time;
                    }
                }

                if (added == 0) break;

                // If we've reached before the earliest needed date, stop
                if (earliestInBatch <= sinceTs) break;

                // Move window back
                toTs = earliestInBatch - 1;

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

        var since = earliest.AddDays(-7).ToUnixTimeSeconds();
        var allCandles = new List<OhlcCandle>();

        var currentSince = since;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var candles = await _krakenApi.GetOhlcDataAsync(krakenPair, currentSince, ct, interval: 240);

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

        var rates = new SortedList<long, decimal>();
        foreach (var candle in allCandles)
            rates[candle.Timestamp] = candle.Close;

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

        // Manual override: user-supplied dated GBP rate(s), interpolated like real rates
        if (_manualOverrides.TryGetValue(fromCurrency, out var manualRates) && manualRates.Count > 0)
        {
            var manualRate = FindClosestRate(manualRates, timestamp);
            if (manualRate != 0)
                return amount * manualRate;
        }

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

        var closestRate = FindClosestRate(rates, timestamp);

        if (pair.Invert && closestRate != 0)
            return 1m / closestRate;

        return closestRate;
    }

    private static decimal FindClosestRate(SortedList<long, decimal> rates, long targetTimestamp)
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

            // Only extend if the cached data doesn't cover the latest needed date.
            // 7-day buffer handles weekends and short data gaps.
            var latestCached = DateTimeOffset.FromUnixTimeSeconds(rates.Keys.Last());
            if (latestCached >= latestNeeded.AddDays(-7))
                return false; // Already sufficient — no download needed

            if (!cacheKey.StartsWith("CC_", StringComparison.OrdinalIgnoreCase))
            {
                await DownloadAndAppendAsync(cacheKey, rates.Keys.Last(), ct);
                return true;
            }

            // CryptoCompare pair is stale — do a full re-download (no append API available)
            var ccParts = cacheKey.Split('_');
            if (ccParts.Length == 3)
            {
                var earliestCc = DateTimeOffset.FromUnixTimeSeconds(rates.Keys.First());
                if (await TryDownloadCryptoCompareAsync(ccParts[1], ccParts[2], cacheKey, earliestCc, ct, forceRefresh: true))
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
            var candles = await _krakenApi.GetOhlcDataAsync(krakenPair, sinceTimestamp, ct, interval: 240);
            if (candles.Count > 0 && _rateCache.TryGetValue(krakenPair, out var existing))
            {
                foreach (var candle in candles)
                    existing[candle.Timestamp] = candle.Close;
                SaveToCache(krakenPair, existing);
            }
        }
        catch
        {
            // Non-critical — we already have cached data
        }
    }

    private bool TryLoadFromCache(string cacheKey)
    {
        var cachePath = Path.Combine(CacheFolder, $"{cacheKey}.json");
        if (!File.Exists(cachePath)) return false;

        try
        {
            var json = File.ReadAllText(cachePath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json);
            if (entries == null || entries.Count == 0) return false;

            // Parse all entries into a list first, then sort by timestamp.
            // SortedList uses a sorted array internally — inserting in random order
            // is O(n) per insert (binary search + element shifting) → O(n²) total.
            // Sorting first ensures ascending-order inserts (end-appends, no shifting).
            var parsed = new List<KeyValuePair<long, decimal>>(entries.Count);
            foreach (var (key, rate) in entries)
            {
                if (long.TryParse(key, out var ts))
                {
                    parsed.Add(new(ts, rate));
                }
                else if (DateOnly.TryParse(key, out var date))
                {
                    var dto = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    parsed.Add(new(dto.ToUnixTimeSeconds(), rate));
                }
            }

            if (parsed.Count == 0) return false;

            parsed.Sort((a, b) => a.Key.CompareTo(b.Key));

            var rates = new SortedList<long, decimal>(parsed.Count);
            foreach (var kv in parsed)
                rates[kv.Key] = kv.Value;

            _rateCache[cacheKey] = rates;

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

    private void SaveToCache(string cacheKey, SortedList<long, decimal> rates)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var dict = rates.ToDictionary(r => r.Key.ToString(), r => r.Value);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
            var path = Path.Combine(CacheFolder, $"{cacheKey}.json");
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

            _pairSources.TryGetValue(pair, out var source);

            results.Add(new FxCacheInfo
            {
                PairName = pair,
                DataPoints = rates.Count,
                EarliestDate = DateTimeOffset.FromUnixTimeSeconds(rates.Keys.First()),
                LatestDate = DateTimeOffset.FromUnixTimeSeconds(rates.Keys.Last()),
                SampleRate = rates.Values.Last(),
                OnDisk = File.Exists(Path.Combine(CacheFolder, $"{pair}.json")),
                DataSource = source ?? "Unknown"
            });
        }

        return results.OrderBy(r => r.PairName).ToList();
    }

    public string CacheFolderPath => CacheFolder;

    /// <summary>
    /// Returns all individual rate data points for a given pair, ordered by date.
    /// Returns empty list if pair not found.
    /// </summary>
    public List<(DateTimeOffset Date, decimal Rate)> GetRateDataPoints(string pairName)
    {
        if (!_rateCache.TryGetValue(pairName, out var rates))
            return new();

        return rates.Select(kv => (
            Date: DateTimeOffset.FromUnixTimeSeconds(kv.Key),
            Rate: kv.Value
        )).ToList();
    }

    /// <summary>
    /// Returns all pair names in the cache.
    /// </summary>
    public List<string> GetPairNames()
    {
        return _rateCache.Keys.Where(k => _rateCache[k].Count > 0).OrderBy(k => k).ToList();
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

    private void LoadManualOverrides()
    {
        if (!File.Exists(ManualOverridesFile)) return;
        try
        {
            var json = File.ReadAllText(ManualOverridesFile);
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
            Directory.CreateDirectory(Path.GetDirectoryName(ManualOverridesFile)!);
            // Serialise as { "BTC": { "1609459200": 25000.0 } }
            var dict = _manualOverrides.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(r => r.Key.ToString(), r => r.Value));
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ManualOverridesFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Scans the cache folder and loads every rate file into memory.
    /// Also reconstructs _pairMap entries so TryGetRate can find them —
    /// this handles the case where pairmap.json doesn't exist yet (first run
    /// after the code change) by deriving mappings from the file names themselves.
    /// </summary>
    public void LoadAllFromDiskCache()
    {
        if (!Directory.Exists(CacheFolder)) return;

        foreach (var file in Directory.GetFiles(CacheFolder, "*.json"))
        {
            var cacheKey = Path.GetFileNameWithoutExtension(file);

            // Skip metadata files
            if (cacheKey.Equals("manual_overrides", StringComparison.OrdinalIgnoreCase)) continue;
            if (cacheKey.Equals("pairmap", StringComparison.OrdinalIgnoreCase)) continue;

            TryLoadFromCache(cacheKey);

            // Ensure _pairMap has an entry so TryGetRate can look up this cache key.
            // TryLoadFromCache sets _pairSources but not _pairMap for dynamic pairs.

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
            else if (KnownPairByCacheKey.TryGetValue(cacheKey, out var known))
            {
                // Known Kraken pair — restore the mapping (constructor already set most of these,
                // but this is a no-op thanks to ContainsKey check)
                var mapKey = (known.Asset, known.Quote);
                if (!_pairMap.ContainsKey(mapKey))
                    _pairMap[mapKey] = (cacheKey, known.Invert);
            }
            // Else: dynamically discovered Kraken pair not in KnownPairs.
            // Will be handled by pairmap.json after the next full FX download.
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
            File.WriteAllText(PairMapFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Restores dynamically discovered pair mappings from the previous session's pairmap.json.
    /// KnownPairs entries are already in _pairMap from the constructor, so they won't be overwritten.
    /// </summary>
    private void LoadPairMap()
    {
        if (!File.Exists(PairMapFile)) return;
        try
        {
            var json = File.ReadAllText(PairMapFile);
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
