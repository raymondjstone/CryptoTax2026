using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

/// <summary>
/// Converts any currency amount to GBP using historical rates from Kraken's public OHLC API.
/// Uses 4-hour candles for better trade-time precision (not end-of-day).
/// Dynamically discovers Kraken pair names for unknown assets.
/// Rates are cached permanently on disk — historical data never expires.
///
/// IMPORTANT: USDT is NOT USD. USDT → USD → GBP (two-step conversion).
/// </summary>
public class FxConversionService
{
    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoTax2026", "fx_cache");

    private readonly KrakenApiService _krakenApi;
    private readonly List<CalculationWarning> _warnings;

    // Cache: key = Kraken pair name, value = sorted list of (unix_timestamp -> close_price)
    // Uses timestamps for sub-daily precision
    private readonly Dictionary<string, SortedList<long, decimal>> _rateCache = new(StringComparer.OrdinalIgnoreCase);

    // Runtime-discovered pair mappings: (asset, quoteCurrency) -> (krakenPair, invert)
    private readonly Dictionary<(string Asset, string Quote), (string KrakenPair, bool Invert)> _pairMap = new();

    // Known Kraken pair names for common conversions
    private static readonly (string From, string To, string KrakenPair, bool Invert)[] KnownPairs = new[]
    {
        // Fiat to GBP
        ("USD", "GBP", "GBPUSD", true),      // Kraken has GBP/USD, invert to get USD→GBP
        ("EUR", "GBP", "GBPEUR", true),       // Kraken has GBP/EUR, invert to get EUR→GBP

        // Stablecoins to USD (NOT to GBP directly - two-step conversion)
        ("USDT", "USD", "USDTUSD", false),
        ("USDC", "USD", "USDCUSD", false),
        ("DAI", "USD", "DAIUSD", false),

        // Major crypto to GBP
        ("BTC", "GBP", "XXBTZGBP", false),
        ("ETH", "GBP", "XETHZGBP", false),
        ("XRP", "GBP", "XXRPZGBP", false),
        ("LTC", "GBP", "XLTCZGBP", false),
        ("ADA", "GBP", "ADAGBP", false),
        ("DOT", "GBP", "DOTGBP", false),
        ("SOL", "GBP", "SOLGBP", false),
        ("DOGE", "GBP", "XDGGBP", false),
        ("LINK", "GBP", "LINKGBP", false),
        ("MATIC", "GBP", "MATICGBP", false),
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

        // Major crypto to USD (fallback if no GBP pair exists)
        ("BTC", "USD", "XXBTZUSD", false),
        ("ETH", "USD", "XETHZUSD", false),
        ("XRP", "USD", "XXRPZUSD", false),
        ("LTC", "USD", "XLTCZUSD", false),
        ("ADA", "USD", "ADAUSD", false),
        ("DOT", "USD", "DOTUSD", false),
        ("SOL", "USD", "SOLUSD", false),
        ("DOGE", "USD", "XDGUSD", false),
        ("LINK", "USD", "LINKUSD", false),
        ("MATIC", "USD", "MATICUSD", false),
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
    };

    // Track which assets we've already warned about to avoid duplicate warnings
    private readonly HashSet<string> _warnedAssets = new(StringComparer.OrdinalIgnoreCase);

    public FxConversionService(KrakenApiService krakenApi, List<CalculationWarning> warnings)
    {
        _krakenApi = krakenApi;
        _warnings = warnings;
        Directory.CreateDirectory(CacheFolder);

        // Populate initial pair map from known pairs
        foreach (var p in KnownPairs)
            _pairMap[(p.From.ToUpperInvariant(), p.To.ToUpperInvariant())] = (p.KrakenPair, p.Invert);
    }

    /// <summary>
    /// Pre-loads all FX rate data needed for the given set of currencies and date range.
    /// Dynamically discovers Kraken pair names for assets not in the known list.
    /// Uses 4-hour OHLC candles and downloads in segments with permanent caching.
    /// </summary>
    public async Task PreloadRatesAsync(
        IEnumerable<string> currencies,
        DateTimeOffset earliest,
        IProgress<(int count, string status)>? progress = null,
        CancellationToken ct = default)
    {
        var neededCurrencies = currencies
            .Select(c => c.ToUpperInvariant())
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
                    pairsToDownload.Add(fx.KrakenPair);
            }
            else if (currency is "USDT" or "USDC" or "DAI")
            {
                if (_pairMap.TryGetValue((currency, "USD"), out var stableFx))
                    pairsToDownload.Add(stableFx.KrakenPair);
                pairsToDownload.Add("GBPUSD");
            }
            else
            {
                // For crypto: add GBP pair if known, otherwise add USD pair
                if (_pairMap.TryGetValue((currency, "GBP"), out var gbpPair))
                {
                    pairsToDownload.Add(gbpPair.KrakenPair);
                }
                else if (_pairMap.TryGetValue((currency, "USD"), out var usdPair))
                {
                    pairsToDownload.Add(usdPair.KrakenPair);
                    pairsToDownload.Add("GBPUSD");
                }
                else
                {
                    // Unknown asset — will try dynamic discovery below
                }
            }
        }

        // Download known pairs first
        int loaded = 0;
        int totalPairs = pairsToDownload.Count;
        foreach (var pair in pairsToDownload.ToList())
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report((loaded, $"Loading FX rates: {pair} ({loaded + 1}/{totalPairs})..."));
            await EnsurePairLoadedAsync(pair, earliest, ct);
            loaded++;
            await Task.Delay(1500, ct);
        }

        // Dynamic discovery for currencies we don't have pairs for
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
                loaded++;

            await Task.Delay(1500, ct);
        }

        progress?.Report((loaded, $"FX rates loaded for {loaded} pairs."));
    }

    /// <summary>
    /// Tries to find a working Kraken pair for an unknown asset by trying common pair name patterns.
    /// </summary>
    private async Task<bool> TryDiscoverPairAsync(string asset, DateTimeOffset earliest, CancellationToken ct)
    {
        // Try these pair name patterns in order (GBP first, then USD as fallback)
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
                var loaded = await TryDownloadPairAsync(pairName, earliest, ct);
                if (loaded)
                {
                    // Successfully found a pair — register it
                    _pairMap[(asset, quote)] = (pairName, invert);
                    if (quote == "USD")
                    {
                        // Also need USD→GBP if not already loaded
                        await EnsurePairLoadedAsync("GBPUSD", earliest, ct);
                    }
                    return true;
                }
            }
            catch
            {
                // Pair doesn't exist, try next candidate
            }

            await Task.Delay(1500, ct);
        }

        // No pair found at all
        if (_warnedAssets.Add(asset))
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Warning,
                Category = "FX Rate",
                Message = $"Could not find any Kraken trading pair for {asset}. Tried {asset}GBP, {asset}USD. " +
                          $"Conversions for this asset will use fallback rates and may be inaccurate."
            });
        }
        return false;
    }

    /// <summary>
    /// Tries to download OHLC data for a pair. Returns true if data was found, false if pair doesn't exist.
    /// </summary>
    private async Task<bool> TryDownloadPairAsync(string krakenPair, DateTimeOffset earliest, CancellationToken ct)
    {
        // Check if already in cache
        if (_rateCache.ContainsKey(krakenPair) && _rateCache[krakenPair].Count > 0)
            return true;

        // Try loading from disk cache
        if (TryLoadFromCache(krakenPair))
            return true;

        // Download from Kraken — use 4-hour candles (interval=240) for better time precision
        var since = earliest.AddDays(-7).ToUnixTimeSeconds();
        var allCandles = new List<OhlcCandle>();

        // Download in segments (Kraken returns max 720 candles per request)
        // 720 * 4 hours = 120 days per segment
        var currentSince = since;
        int emptyResponses = 0;
        while (!ct.IsCancellationRequested)
        {
            var candles = await _krakenApi.GetOhlcDataAsync(krakenPair, currentSince, ct, interval: 240);

            if (candles.Count == 0)
            {
                emptyResponses++;
                if (emptyResponses > 1) break; // pair likely doesn't exist
                break;
            }

            allCandles.AddRange(candles);

            // If we got fewer than 720 candles, we've reached the end
            if (candles.Count < 720)
                break;

            // Continue from after the last candle
            currentSince = candles.Last().Timestamp;
            await Task.Delay(1500, ct); // rate limit
        }

        if (allCandles.Count == 0)
            return false;

        var rates = new SortedList<long, decimal>();
        foreach (var candle in allCandles)
        {
            rates[candle.Timestamp] = candle.Close;
        }

        _rateCache[krakenPair] = rates;
        SaveToCache(krakenPair, rates);
        return true;
    }

    /// <summary>
    /// Converts an amount from the given currency to GBP at the given date/time.
    /// Uses the closest available rate to the actual transaction time.
    /// </summary>
    public decimal ConvertToGbp(decimal amount, string fromCurrency, DateTimeOffset date)
    {
        fromCurrency = fromCurrency.ToUpperInvariant();

        if (fromCurrency == "GBP")
            return amount;

        if (amount == 0)
            return 0;

        var timestamp = date.ToUnixTimeSeconds();

        // ---- Fiat currencies ----
        if (fromCurrency == "USD")
            return amount * GetRate("USD", "GBP", timestamp);

        if (fromCurrency == "EUR")
            return amount * GetRate("EUR", "GBP", timestamp);

        // ---- Stablecoins: convert to USD first, then USD to GBP ----
        if (fromCurrency is "USDT" or "USDC" or "DAI")
        {
            var usdAmount = amount * GetRate(fromCurrency, "USD", timestamp);
            return usdAmount * GetRate("USD", "GBP", timestamp);
        }

        // ---- Crypto: try direct GBP pair, fallback to USD pair + USD/GBP ----
        var gbpRate = TryGetRate(fromCurrency, "GBP", timestamp);
        if (gbpRate.HasValue)
            return amount * gbpRate.Value;

        var usdRate = TryGetRate(fromCurrency, "USD", timestamp);
        if (usdRate.HasValue)
        {
            var usdVal = amount * usdRate.Value;
            return usdVal * GetRate("USD", "GBP", timestamp);
        }

        // No rate available — only warn once per asset to avoid spam
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

        // Return 0 instead of the raw amount — returning the quantity as GBP value
        // causes wildly incorrect tax calculations (e.g. 0.5 ETH treated as £0.50)
        return 0m;
    }

    /// <summary>
    /// Gets the GBP value of a crypto asset on a given date (for valuing staking rewards etc).
    /// </summary>
    public decimal GetGbpValueOfAsset(string asset, decimal quantity, DateTimeOffset date)
    {
        return ConvertToGbp(quantity, asset, date);
    }

    private decimal GetRate(string from, string to, long timestamp)
    {
        var rate = TryGetRate(from, to, timestamp);
        if (rate.HasValue) return rate.Value;

        // Fallback — only warn once
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

        if (!_rateCache.TryGetValue(pair.KrakenPair, out var rates) || rates.Count == 0)
            return null;

        // Find the closest rate to this timestamp using binary search
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

        // Binary search for closest timestamp on or before target
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

        if (bestBefore >= 0)
        {
            // Check if the next entry is closer
            if (bestBefore + 1 < keys.Count)
            {
                var diffBefore = targetTimestamp - keys[bestBefore];
                var diffAfter = keys[bestBefore + 1] - targetTimestamp;
                if (diffAfter < diffBefore)
                    return rates.Values[bestBefore + 1];
            }
            return rates.Values[bestBefore];
        }

        // All rates are after this timestamp — use the earliest
        if (keys.Count > 0)
            return rates.Values[0];

        return 0m;
    }

    private async Task EnsurePairLoadedAsync(string krakenPair, DateTimeOffset earliest, CancellationToken ct)
    {
        // Check memory cache first
        if (_rateCache.ContainsKey(krakenPair) && _rateCache[krakenPair].Count > 0)
            return;

        // Try loading from disk cache
        if (TryLoadFromCache(krakenPair))
        {
            // Check if we need to extend the cache with newer data
            var cached = _rateCache[krakenPair];
            var latestCached = DateTimeOffset.FromUnixTimeSeconds(cached.Keys.Last());
            var staleness = DateTimeOffset.UtcNow - latestCached;

            // Only re-download if cache is more than 7 days stale
            if (staleness.TotalDays <= 7)
                return;

            // Download only new data from where cache ends
            await DownloadAndAppendAsync(krakenPair, cached.Keys.Last(), ct);
            return;
        }

        // No cache at all — download everything
        await TryDownloadPairAsync(krakenPair, earliest, ct);
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

    private bool TryLoadFromCache(string krakenPair)
    {
        var cachePath = Path.Combine(CacheFolder, $"{krakenPair}.json");
        if (!File.Exists(cachePath)) return false;

        try
        {
            var json = File.ReadAllText(cachePath);

            // Try new format first (timestamp-based)
            var entries = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json);
            if (entries == null || entries.Count == 0) return false;

            var rates = new SortedList<long, decimal>();
            foreach (var (key, rate) in entries)
            {
                // Support both formats: unix timestamp string or date string
                if (long.TryParse(key, out var ts))
                {
                    rates[ts] = rate;
                }
                else if (DateOnly.TryParse(key, out var date))
                {
                    // Legacy format: convert date to midnight UTC timestamp
                    var dto = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    rates[dto.ToUnixTimeSeconds()] = rate;
                }
            }

            if (rates.Count == 0) return false;

            _rateCache[krakenPair] = rates;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveToCache(string krakenPair, SortedList<long, decimal> rates)
    {
        try
        {
            var dict = rates.ToDictionary(r => r.Key.ToString(), r => r.Value);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(Path.Combine(CacheFolder, $"{krakenPair}.json"), json);
        }
        catch
        {
            // Non-critical
        }
    }
}
