using System;
using System.Collections.Generic;
using System.Reflection;
using CryptoTax2026.Models;
using CryptoTax2026.Services;

namespace CryptoTax2026.Tests.Helpers;

/// <summary>
/// Creates an FxConversionService with pre-loaded rates for testing,
/// bypassing the need for actual Kraken API calls.
/// </summary>
public static class TestFxHelper
{
    /// <summary>
    /// Pair map that matches the cache keys used by <see cref="CreateWithDefaultRates"/>.
    /// Must be kept in sync whenever default cache keys change.
    /// </summary>
    public static readonly Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)> DefaultPairMap =
        new()
        {
            // Fiat — GBPUSD is stored as GBP base so invert to get USD→GBP
            [("USD", "GBP")] = ("GBPUSD",   true),
            // EUR/GBP stored as direct EUR→GBP rate
            [("EUR", "GBP")] = ("EURGBP",   false),
            // Stablecoins via USD
            [("USDT", "USD")] = ("USDTUSD", false),
            [("USDC", "USD")] = ("USDCUSD", false),
            [("DAI",  "USD")] = ("DAIUSD",  false),
            // Crypto direct GBP pairs
            [("BTC", "GBP")] = ("XXBTZGBP", false),
            [("ETH", "GBP")] = ("XETHZGBP", false),
            [("XRP", "GBP")] = ("XXRPZGBP", false),
            [("ADA", "GBP")] = ("ADAGBP",   false),
            [("DOT", "GBP")] = ("DOTGBP",   false),
            [("SOL", "GBP")] = ("SOLGBP",   false),
            // Crypto USD fallback pairs
            [("BTC", "USD")] = ("XXBTZUSD", false),
            [("ETH", "USD")] = ("XETHZUSD", false),
        };

    /// <summary>
    /// Creates an FxConversionService with decimal rates and an explicit pair map injected.
    /// The pair map is required; without it <see cref="FxConversionService.ConvertToGbp"/> will
    /// always return the fallback value (1.0 / warning) regardless of what is in the cache.
    /// </summary>
    public static FxConversionService CreateWithRates(
        List<CalculationWarning> warnings,
        Dictionary<string, SortedList<long, decimal>>? rates = null,
        Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)>? pairMap = null)
    {
        var service = new FxConversionService(null!, warnings, null, FxRateType.Average);

        var cache = GetRateCache(service);

        if (rates != null)
        {
            foreach (var (pair, pairRates) in rates)
            {
                var ohlcRates = new SortedList<long, OhlcCandle>(pairRates.Count);
                foreach (var (timestamp, rate) in pairRates)
                {
                    ohlcRates[timestamp] = new OhlcCandle
                    {
                        Timestamp = timestamp,
                        Open = rate,
                        High = rate,
                        Low = rate,
                        Close = rate
                    };
                }
                cache[pair] = ohlcRates;
            }
        }

        if (pairMap != null)
        {
            var map = GetPairMap(service);
            foreach (var kv in pairMap)
                map[kv.Key] = kv.Value;
        }

        return service;
    }

    /// <summary>
    /// Creates an FxConversionService with full OHLC candle data injected directly.
    /// Use this when tests need O/H/L/C values to differ (e.g. testing FxRateType selection).
    /// </summary>
    public static FxConversionService CreateWithOhlcRates(
        List<CalculationWarning> warnings,
        FxRateType rateType,
        Dictionary<string, SortedList<long, OhlcCandle>> ohlcRates,
        Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)> pairMap)
    {
        var service = new FxConversionService(null!, warnings, null, rateType);

        var cache = GetRateCache(service);
        foreach (var kv in ohlcRates)
            cache[kv.Key] = kv.Value;

        var map = GetPairMap(service);
        foreach (var kv in pairMap)
            map[kv.Key] = kv.Value;

        return service;
    }

    /// <summary>
    /// Creates an FxConversionService with a simple fixed rate for common pairs.
    /// USD/GBP = 0.80 (GBPUSD inverted = 1.25), USDT/USD = 1.00, BTC/GBP = 30000, ETH/GBP = 2000.
    /// </summary>
    public static FxConversionService CreateWithDefaultRates(List<CalculationWarning> warnings)
    {
        var rates = new Dictionary<string, SortedList<long, decimal>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GBPUSD"]   = MakeConstantRates(1.25m),
            ["EURGBP"]   = MakeConstantRates(1m / 1.16m),
            ["USDTUSD"]  = MakeConstantRates(1.00m),
            ["USDCUSD"]  = MakeConstantRates(1.00m),
            ["DAIUSD"]   = MakeConstantRates(1.00m),
            ["XXBTZGBP"] = MakeConstantRates(30000m),
            ["XETHZGBP"] = MakeConstantRates(2000m),
            ["XXRPZGBP"] = MakeConstantRates(0.50m),
            ["ADAGBP"]   = MakeConstantRates(0.40m),
            ["DOTGBP"]   = MakeConstantRates(5.00m),
            ["SOLGBP"]   = MakeConstantRates(80.00m),
            ["XXBTZUSD"] = MakeConstantRates(37500m),
            ["XETHZUSD"] = MakeConstantRates(2500m),
        };

        return CreateWithRates(warnings, rates, DefaultPairMap);
    }

    public static SortedList<long, decimal> MakeConstantRates(decimal rate)
    {
        var start = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var end   = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var days  = (int)((end - start) / 86400) + 1;
        var rates = new SortedList<long, decimal>(days);
        for (var ts = start; ts <= end; ts += 86400)
            rates[ts] = rate;
        return rates;
    }

    private static Dictionary<string, SortedList<long, OhlcCandle>> GetRateCache(FxConversionService service)
    {
        var field = typeof(FxConversionService).GetField("_rateCache", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find _rateCache field on FxConversionService");
        return (Dictionary<string, SortedList<long, OhlcCandle>>)field.GetValue(service)!;
    }

    private static Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)> GetPairMap(FxConversionService service)
    {
        var field = typeof(FxConversionService).GetField("_pairMap", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find _pairMap field on FxConversionService");
        return (Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)>)field.GetValue(service)!;
    }
}
