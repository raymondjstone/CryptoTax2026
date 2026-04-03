using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

/// <summary>
/// Loads <c>kraken_delisted.csv</c> (shipped alongside the executable) and provides two services:
/// <list type="bullet">
///   <item>FX price fallback — daily OHLC rates for delisted Kraken pairs, used by
///         <see cref="FxConversionService"/> when the live Kraken API has no data.</item>
///   <item>Delist-date derivation — the last price date per pair + 1 day is taken as the
///         assumed delist date, providing more accurate dates than the supplementary
///         <c>kraken_pairs_events.json</c> database.</item>
/// </list>
/// </summary>
public class DelistedPriceService
{
    // pair altname (UPPER) → sorted daily OHLC candles
    private readonly Dictionary<string, SortedList<long, OhlcCandle>> _pairData
        = new(StringComparer.OrdinalIgnoreCase);

    // (normalised base asset, normalised quote) → pair altname
    // Only populated for GBP / USD / EUR / USDT / USDC-quoted pairs (FX-useful)
    private readonly Dictionary<(string Base, string Quote), string> _fxLookup = new();

    /// <summary>The latest data date across all pairs in the CSV.</summary>
    public DateOnly LatestDataDate { get; private set; }

    /// <summary>Number of pairs loaded from the CSV.</summary>
    public int PairCount => _pairData.Count;

    private DelistedPriceService() { }

    // ─────────────────────────────── Factory ─────────────────────────────────

    /// <summary>
    /// Loads the delisted-pairs CSV from the application's Assets folder (or a custom path).
    /// Returns <c>null</c> if the file is absent or unreadable.
    /// </summary>
    public static DelistedPriceService? TryLoad(string? csvPath = null)
    {
        var path = csvPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "kraken_delisted.csv");
        if (!File.Exists(path))
            return null;

        try
        {
            var svc = new DelistedPriceService();
            svc.LoadFromCsv(path);
            return svc.PairCount > 0 ? svc : null;
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────── CSV parsing ───────────────────────────────

    private void LoadFromCsv(string path)
    {
        // Accumulate candles per pair before sorting
        var tempData = new Dictionary<string, List<OhlcCandle>>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(path);

        // Skip header line: pair,timestamp,open,high,low,close,vwap,volume,count
        reader.ReadLine();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(',');
            if (parts.Length < 6) continue;

            var pair = parts[0].Trim();
            if (string.IsNullOrEmpty(pair)) continue;

            if (!long.TryParse(parts[1].Trim(), out var ts)) continue;
            if (!decimal.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
            if (!decimal.TryParse(parts[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
            if (!decimal.TryParse(parts[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
            if (!decimal.TryParse(parts[5].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;

            if (!tempData.TryGetValue(pair, out var list))
            {
                list = new List<OhlcCandle>();
                tempData[pair] = list;
            }
            list.Add(new OhlcCandle { Timestamp = ts, Open = open, High = high, Low = low, Close = close });
        }

        var maxDate = DateOnly.MinValue;

        foreach (var (pair, candles) in tempData)
        {
            var sorted = new SortedList<long, OhlcCandle>(candles.Count);
            foreach (var c in candles)
                sorted[c.Timestamp] = c;

            var upper = pair.ToUpperInvariant();
            _pairData[upper] = sorted;

            // Track the overall latest date
            var lastTs = sorted.Keys[sorted.Count - 1];
            var lastDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(lastTs).UtcDateTime);
            if (lastDate > maxDate) maxDate = lastDate;

            // Register FX lookup for GBP / USD / EUR / USDT / USDC-quoted pairs
            if (TryParseQuote(upper, out var basePart, out var quotePart))
            {
                var normQuote = NormalizeQuote(quotePart);
                if (normQuote is "GBP" or "USD" or "EUR" or "USDT" or "USDC")
                {
                    _fxLookup.TryAdd((basePart, normQuote), upper);

                    // Also register the full pair altname as a base alias.
                    // Kraken ledgers sometimes store the pair name as the asset ticker
                    // (e.g. asset "KUSD" for the K/USD pair instead of "K").
                    if (basePart != upper)
                        _fxLookup.TryAdd((upper, normQuote), upper);
                }
            }
        }

        LatestDataDate = maxDate;
    }

    // ──────────────────────────── Pair parsing ───────────────────────────────

    // Quote suffixes checked longest-first so "USDT" beats "USD", "ZGBP" beats "GBP", etc.
    private static readonly string[] KnownQuoteSuffixes =
    [
        "USDT", "USDC", "ZGBP", "ZUSD", "ZEUR", "XAUD", "XJPY",
        "GBP", "USD", "EUR", "XBT", "ETH", "JPY", "AUD", "CAD", "CHF"
    ];

    private static bool TryParseQuote(string upperPair, out string basePart, out string quotePart)
    {
        foreach (var q in KnownQuoteSuffixes)
        {
            if (upperPair.Length > q.Length && upperPair.EndsWith(q, StringComparison.Ordinal))
            {
                basePart = upperPair[..^q.Length];
                quotePart = q;
                return true;
            }
        }
        basePart = upperPair;
        quotePart = "";
        return false;
    }

    private static string NormalizeQuote(string q) => q switch
    {
        "ZGBP" => "GBP",
        "ZUSD" => "USD",
        "ZEUR" => "EUR",
        _ => q
    };

    // ──────────────────────────── FX lookups ─────────────────────────────────

    /// <summary>
    /// Returns the closest OHLC candle at or before <paramref name="timestamp"/> for the given
    /// (base asset, quote currency) pair.  Only GBP / USD / EUR / USDT / USDC quotes are
    /// registered; returns <c>null</c> for unrecognised pairs.
    /// </summary>
    public OhlcCandle? TryGetRate(string baseAsset, string quoteAsset, long timestamp)
    {
        if (!_fxLookup.TryGetValue(
                (baseAsset.ToUpperInvariant(), quoteAsset.ToUpperInvariant()),
                out var pairName))
            return null;

        if (!_pairData.TryGetValue(pairName, out var rates) || rates.Count == 0)
            return null;

        return FindClosestCandle(rates, timestamp);
    }

    private static OhlcCandle? FindClosestCandle(SortedList<long, OhlcCandle> rates, long timestamp)
    {
        // Prefer the candle whose day-start matches the target day
        var targetDate = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        var dayStart = new DateTimeOffset(targetDate.Year, targetDate.Month, targetDate.Day,
                                         0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        if (rates.TryGetValue(dayStart, out var exact))
            return exact;

        // Fallback: binary search for most-recent candle at or before timestamp
        var keys = rates.Keys;
        int lo = 0, hi = keys.Count - 1, bestBefore = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] <= timestamp) { bestBefore = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (bestBefore >= 0) return rates.Values[bestBefore];
        if (keys.Count > 0) return rates.Values[0]; // earliest available
        return null;
    }

    // ─────────────────────────── Delist dates ────────────────────────────────

    /// <summary>
    /// Returns the assumed delist date for a pair: the last data date in the CSV + 1 day.
    /// Returns <c>null</c> if the pair is not in the CSV.
    /// </summary>
    public DateOnly? GetDelistDate(string pairAltname)
    {
        if (!_pairData.TryGetValue(pairAltname.ToUpperInvariant(), out var data) || data.Count == 0)
            return null;
        var lastTs = data.Keys[data.Count - 1];
        return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(lastTs).UtcDateTime).AddDays(1);
    }

    /// <summary>
    /// Generates a <see cref="DelistedAssetEvent"/> for every pair in the CSV.
    /// The delist date is the last CSV price date + 1 day (accurate, no ~).
    /// Relist dates are not available from the CSV — callers should merge with JSON data.
    /// </summary>
    public List<DelistedAssetEvent> GetDelistEvents()
    {
        var result = new List<DelistedAssetEvent>(_pairData.Count);
        foreach (var (pair, data) in _pairData)
        {
            if (data.Count == 0) continue;
            var lastTs = data.Keys[data.Count - 1];
            var lastDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(lastTs).UtcDateTime);
            var delistDate = new DateTimeOffset(
                lastDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            result.Add(new DelistedAssetEvent
            {
                Pair = pair,
                DelistingDate = delistDate,
                RelistDate = null,
                Notes = "Kraken",
                ClaimType = "Delisted"
            });
        }
        return result;
    }

    // ────────────────────────── Pair data access ─────────────────────────────

    /// <summary>
    /// Returns all pair names in the CSV dataset.
    /// </summary>
    public IEnumerable<string> GetPairNames() => _pairData.Keys;

    /// <summary>
    /// Returns the CSV pair altname for a given base/quote combination, or <c>null</c> if not
    /// registered.  Only GBP / USD / EUR / USDT / USDC-quoted pairs are registered.
    /// </summary>
    public string? GetPairAltname(string baseAsset, string quoteAsset)
        => _fxLookup.TryGetValue(
               (baseAsset.ToUpperInvariant(), quoteAsset.ToUpperInvariant()), out var name)
           ? name : null;

    /// <summary>
    /// Returns all base-currency tickers registered for <paramref name="pairAltname"/> in the
    /// FX lookup table (e.g. both "K" and "KUSD" for the K/USD pair).
    /// Used by <see cref="FxConversionService"/> to decide whether a CSV pair is relevant
    /// for the current ledger.
    /// </summary>
    public IEnumerable<string> GetBaseCurrenciesForPair(string pairAltname)
    {
        var upper = pairAltname.ToUpperInvariant();
        foreach (var (key, value) in _fxLookup)
            if (string.Equals(value, upper, StringComparison.OrdinalIgnoreCase))
                yield return key.Base;
    }

    /// <summary>
    /// Returns the raw candle data for a pair by its altname, or <c>null</c> if not found.
    /// </summary>
    public SortedList<long, OhlcCandle>? GetPairCandles(string pairAltname)
        => _pairData.TryGetValue(pairAltname.ToUpperInvariant(), out var data) ? data : null;

    // ─────────────────────────── Staleness check ─────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when <see cref="LatestDataDate"/> is before the end of the current
    /// UK tax year (5 April).  Outputs <paramref name="taxYearEnd"/> so callers can display it.
    /// This is expected to be <c>true</c> until the CSV is updated.
    /// </summary>
    public bool IsDataStale(out DateOnly taxYearEnd)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // UK tax year ends 5 April; if today is 6 April or later the current TY ends next year
        taxYearEnd = (today.Month > 4 || (today.Month == 4 && today.Day > 5))
            ? new DateOnly(today.Year + 1, 4, 5)
            : new DateOnly(today.Year, 4, 5);
        return LatestDataDate < taxYearEnd;
    }
}
