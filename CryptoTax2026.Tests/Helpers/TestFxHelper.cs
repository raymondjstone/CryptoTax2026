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
    /// Creates an FxConversionService with fixed rates injected.
    /// </summary>
    public static FxConversionService CreateWithRates(
        List<CalculationWarning> warnings,
        Dictionary<string, SortedList<long, decimal>>? rates = null)
    {
        var service = new FxConversionService(null!, warnings, null, FxRateType.Average);

        var cache = GetRateCache(service);

        if (rates != null)
        {
            foreach (var (pair, pairRates) in rates)
            {
                // Convert decimal rates to OHLC candles for new cache format
                var ohlcRates = new SortedList<long, OhlcCandle>();
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

        return service;
    }

    /// <summary>
    /// Creates an FxConversionService with a simple fixed rate for common pairs.
    /// USD/GBP = 0.80 (GBPUSD inverted = 1.25), USDT/USD = 1.00, BTC/GBP = 30000, ETH/GBP = 2000
    /// </summary>
    public static FxConversionService CreateWithDefaultRates(List<CalculationWarning> warnings)
    {
        var rates = new Dictionary<string, SortedList<long, decimal>>(StringComparer.OrdinalIgnoreCase);

        // GBP/USD pair (inverted: USD->GBP = 1/1.25 = 0.80)
        rates["GBPUSD"] = MakeConstantRates(1.25m);

        // EUR/GBP pair (direct: EUR->GBP = ~0.8621)
        rates["EURGBP"] = MakeConstantRates(1m / 1.16m);

        // Stablecoin/USD pairs (not inverted)
        rates["USDTUSD"] = MakeConstantRates(1.00m);
        rates["USDCUSD"] = MakeConstantRates(1.00m);
        rates["DAIUSD"] = MakeConstantRates(1.00m);

        // Crypto/GBP pairs
        rates["XXBTZGBP"] = MakeConstantRates(30000m);
        rates["XETHZGBP"] = MakeConstantRates(2000m);
        rates["XXRPZGBP"] = MakeConstantRates(0.50m);
        rates["ADAGBP"] = MakeConstantRates(0.40m);
        rates["DOTGBP"] = MakeConstantRates(5.00m);
        rates["SOLGBP"] = MakeConstantRates(80.00m);

        // Crypto/USD pairs (fallback)
        rates["XXBTZUSD"] = MakeConstantRates(37500m);
        rates["XETHZUSD"] = MakeConstantRates(2500m);

        return CreateWithRates(warnings, rates);
    }

    private static SortedList<long, decimal> MakeConstantRates(decimal rate)
    {
        var rates = new SortedList<long, decimal>();
        // Create entries every 4 hours from 2020-01-01 to 2026-12-31
        var start = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var end = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        for (var ts = start; ts <= end; ts += 86400) // daily
        {
            rates[ts] = rate;
        }
        return rates;
    }

    private static Dictionary<string, SortedList<long, OhlcCandle>> GetRateCache(FxConversionService service)
    {
        var field = typeof(FxConversionService).GetField("_rateCache", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find _rateCache field on FxConversionService");
        return (Dictionary<string, SortedList<long, OhlcCandle>>)field.GetValue(service)!;
    }
}
