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
/// Converts any currency amount to GBP using historical daily rates from Kraken's public OHLC API.
/// Rates are cached locally to avoid re-downloading.
///
/// IMPORTANT: USDT is NOT USD. USDT is a stablecoin that is usually close to $1 but not always.
/// We convert USDT -> USD first (using USDT/USD rate) then USD -> GBP.
/// </summary>
public class FxConversionService
{
    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoTax2026", "fx_cache");

    private readonly KrakenApiService _krakenApi;
    private readonly List<CalculationWarning> _warnings;

    // Cache: key = "PAIR", value = sorted list of daily rates (date -> close price)
    private readonly Dictionary<string, SortedList<DateOnly, decimal>> _rateCache = new(StringComparer.OrdinalIgnoreCase);

    // Kraken pair names for FX conversions we need
    // These map (from_currency, to_currency) -> Kraken pair name and whether to invert
    private static readonly (string From, string To, string KrakenPair, bool Invert)[] FxPairs = new[]
    {
        // Fiat to GBP
        ("USD", "GBP", "GBPUSD", true),      // Kraken has GBP/USD, invert to get USD->GBP
        ("EUR", "GBP", "GBPEUR", true),       // Kraken has GBP/EUR, invert to get EUR->GBP

        // Stablecoins to USD (NOT to GBP directly - two-step conversion)
        ("USDT", "USD", "USDTUSD", false),    // USDT/USD rate (usually ~1.0 but NOT always)
        ("USDC", "USD", "USDCUSD", false),    // USDC/USD rate
        ("DAI", "USD", "DAIUSD", false),       // DAI/USD rate

        // Major crypto to GBP (for valuing staking rewards etc.)
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
    };

    public FxConversionService(KrakenApiService krakenApi, List<CalculationWarning> warnings)
    {
        _krakenApi = krakenApi;
        _warnings = warnings;
        Directory.CreateDirectory(CacheFolder);
    }

    /// <summary>
    /// Pre-loads all FX rate data needed for the given set of currencies and date range.
    /// Call this before doing conversions to batch the downloads.
    /// </summary>
    public async Task PreloadRatesAsync(
        IEnumerable<string> currencies,
        DateTimeOffset earliest,
        IProgress<(int count, string status)>? progress = null,
        CancellationToken ct = default)
    {
        var neededCurrencies = currencies
            .Select(c => c.ToUpperInvariant())
            .Where(c => c != "GBP") // GBP doesn't need conversion
            .Distinct()
            .ToList();

        // Figure out which Kraken pairs we need to download
        var pairsToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var currency in neededCurrencies)
        {
            if (currency is "USD" or "EUR")
            {
                // Direct fiat->GBP
                var fx = FxPairs.FirstOrDefault(p => p.From == currency && p.To == "GBP");
                if (fx != default) pairsToDownload.Add(fx.KrakenPair);
            }
            else if (currency is "USDT" or "USDC" or "DAI")
            {
                // Stablecoin -> USD -> GBP (two hops)
                var stableFx = FxPairs.FirstOrDefault(p => p.From == currency && p.To == "USD");
                if (stableFx != default) pairsToDownload.Add(stableFx.KrakenPair);
                var usdGbp = FxPairs.FirstOrDefault(p => p.From == "USD" && p.To == "GBP");
                if (usdGbp != default) pairsToDownload.Add(usdGbp.KrakenPair);
            }
            else
            {
                // Crypto -> try GBP pair first, then USD pair + USD/GBP
                var gbpPair = FxPairs.FirstOrDefault(p => p.From == currency && p.To == "GBP");
                if (gbpPair != default)
                {
                    pairsToDownload.Add(gbpPair.KrakenPair);
                }
                else
                {
                    var usdPair = FxPairs.FirstOrDefault(p => p.From == currency && p.To == "USD");
                    if (usdPair != default)
                    {
                        pairsToDownload.Add(usdPair.KrakenPair);
                        var usdGbp = FxPairs.FirstOrDefault(p => p.From == "USD" && p.To == "GBP");
                        if (usdGbp != default) pairsToDownload.Add(usdGbp.KrakenPair);
                    }
                }
            }
        }

        int loaded = 0;
        foreach (var pair in pairsToDownload)
        {
            if (ct.IsCancellationRequested) break;

            progress?.Report((loaded, $"Loading FX rates: {pair}..."));
            await EnsurePairLoadedAsync(pair, earliest, ct);
            loaded++;

            // Respect public API rate limit (~1 req/sec)
            await Task.Delay(1500, ct);
        }

        progress?.Report((loaded, $"FX rates loaded for {loaded} pairs."));
    }

    /// <summary>
    /// Converts an amount from the given currency to GBP at the given date.
    /// Returns the GBP equivalent, or the original amount with a warning if conversion fails.
    /// </summary>
    public decimal ConvertToGbp(decimal amount, string fromCurrency, DateTimeOffset date)
    {
        fromCurrency = fromCurrency.ToUpperInvariant();

        if (fromCurrency == "GBP")
            return amount;

        if (amount == 0)
            return 0;

        var dateOnly = DateOnly.FromDateTime(date.UtcDateTime);

        // ---- Fiat currencies ----
        if (fromCurrency == "USD")
        {
            return amount * GetRate("USD", "GBP", dateOnly);
        }

        if (fromCurrency == "EUR")
        {
            return amount * GetRate("EUR", "GBP", dateOnly);
        }

        // ---- Stablecoins: convert to USD first, then USD to GBP ----
        if (fromCurrency is "USDT" or "USDC" or "DAI")
        {
            var usdAmount = amount * GetRate(fromCurrency, "USD", dateOnly);
            return usdAmount * GetRate("USD", "GBP", dateOnly);
        }

        // ---- Crypto: try direct GBP pair, fallback to USD pair + USD/GBP ----
        var gbpRate = TryGetRate(fromCurrency, "GBP", dateOnly);
        if (gbpRate.HasValue)
            return amount * gbpRate.Value;

        var usdRate = TryGetRate(fromCurrency, "USD", dateOnly);
        if (usdRate.HasValue)
        {
            var usdVal = amount * usdRate.Value;
            return usdVal * GetRate("USD", "GBP", dateOnly);
        }

        // No rate available
        _warnings.Add(new CalculationWarning
        {
            Level = WarningLevel.Error,
            Category = "FX Rate",
            Message = $"No GBP or USD exchange rate available for {fromCurrency} on {dateOnly:dd/MM/yyyy}. Using amount as-is (likely incorrect).",
            Date = date,
            Asset = fromCurrency
        });

        return amount;
    }

    /// <summary>
    /// Gets the GBP value of a crypto asset on a given date (for valuing staking rewards etc).
    /// </summary>
    public decimal GetGbpValueOfAsset(string asset, decimal quantity, DateTimeOffset date)
    {
        return ConvertToGbp(quantity, asset, date);
    }

    private decimal GetRate(string from, string to, DateOnly date)
    {
        var rate = TryGetRate(from, to, date);
        if (rate.HasValue) return rate.Value;

        _warnings.Add(new CalculationWarning
        {
            Level = WarningLevel.Warning,
            Category = "FX Rate",
            Message = $"No {from}/{to} rate found for {date:dd/MM/yyyy}. Using nearest available rate.",
            Date = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            Asset = from
        });

        return 1m; // Fallback - will be wrong but at least won't crash
    }

    private decimal? TryGetRate(string from, string to, DateOnly date)
    {
        var fx = FxPairs.FirstOrDefault(p =>
            p.From.Equals(from, StringComparison.OrdinalIgnoreCase) &&
            p.To.Equals(to, StringComparison.OrdinalIgnoreCase));

        if (fx == default) return null;

        if (!_rateCache.TryGetValue(fx.KrakenPair, out var rates) || rates.Count == 0)
            return null;

        // Find the closest rate on or before this date
        decimal closestRate;
        var idx = FindClosestIndex(rates, date);
        if (idx >= 0)
        {
            closestRate = rates.Values[idx];
        }
        else
        {
            // All rates are after this date - use the earliest we have
            closestRate = rates.Values[0];
        }

        // If the pair is inverted (e.g. GBP/USD -> need USD/GBP), invert the rate
        if (fx.Invert && closestRate != 0)
            return 1m / closestRate;

        return closestRate;
    }

    private int FindClosestIndex(SortedList<DateOnly, decimal> rates, DateOnly target)
    {
        // Binary search for the closest date on or before target
        var keys = rates.Keys;
        int lo = 0, hi = keys.Count - 1;
        int best = -1;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] <= target)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // If no exact or earlier date, try the next available date
        if (best < 0 && keys.Count > 0)
            best = 0;

        return best;
    }

    private async Task EnsurePairLoadedAsync(string krakenPair, DateTimeOffset earliest, CancellationToken ct)
    {
        // Try loading from cache file first
        if (TryLoadFromCache(krakenPair))
            return;

        // Download from Kraken public API
        try
        {
            var since = earliest.AddDays(-7).ToUnixTimeSeconds(); // Get a bit of extra history
            var candles = await _krakenApi.GetOhlcDataAsync(krakenPair, since, ct);

            if (candles.Count == 0)
            {
                _warnings.Add(new CalculationWarning
                {
                    Level = WarningLevel.Warning,
                    Category = "FX Rate",
                    Message = $"No OHLC data returned from Kraken for pair {krakenPair}. Conversions using this pair may be inaccurate."
                });
                return;
            }

            var rates = new SortedList<DateOnly, decimal>();
            foreach (var candle in candles)
            {
                var date = DateOnly.FromDateTime(candle.DateTime.UtcDateTime);
                rates[date] = candle.Close;
            }

            _rateCache[krakenPair] = rates;
            SaveToCache(krakenPair, rates);
        }
        catch (Exception ex)
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Warning,
                Category = "FX Rate",
                Message = $"Failed to download OHLC data for {krakenPair}: {ex.Message}. Will attempt to use cached data."
            });

            // Try cache even if stale
            TryLoadFromCache(krakenPair);
        }
    }

    private bool TryLoadFromCache(string krakenPair)
    {
        var cachePath = Path.Combine(CacheFolder, $"{krakenPair}.json");
        if (!File.Exists(cachePath)) return false;

        // Use cache if less than 24 hours old
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
        if (age.TotalHours > 24) return false;

        try
        {
            var json = File.ReadAllText(cachePath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json);
            if (entries == null || entries.Count == 0) return false;

            var rates = new SortedList<DateOnly, decimal>();
            foreach (var (dateStr, rate) in entries)
            {
                if (DateOnly.TryParse(dateStr, out var date))
                    rates[date] = rate;
            }

            _rateCache[krakenPair] = rates;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveToCache(string krakenPair, SortedList<DateOnly, decimal> rates)
    {
        try
        {
            var dict = rates.ToDictionary(r => r.Key.ToString("yyyy-MM-dd"), r => r.Value);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(CacheFolder, $"{krakenPair}.json"), json);
        }
        catch
        {
            // Non-critical
        }
    }
}
