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
    /// Creates an FxConversionService with fixed GBP rates injected.
    /// Supports USD/GBP, EUR/GBP, USDT/USD, and crypto/GBP or crypto/USD pairs.
    /// </summary>
    public static FxConversionService CreateWithRates(
        List<CalculationWarning> warnings,
        Dictionary<string, SortedList<DateOnly, decimal>>? rates = null)
    {
        var service = new FxConversionService(null!, warnings);

        var cache = GetRateCache(service);

        if (rates != null)
        {
            foreach (var (pair, pairRates) in rates)
                cache[pair] = pairRates;
        }

        return service;
    }

    /// <summary>
    /// Creates an FxConversionService with a simple fixed rate for common pairs.
    /// USD/GBP = 0.80, EUR/GBP = 0.86, USDT/USD = 1.00, BTC/GBP = 30000, ETH/GBP = 2000
    /// </summary>
    public static FxConversionService CreateWithDefaultRates(List<CalculationWarning> warnings)
    {
        var dateRange = new SortedList<DateOnly, decimal>();
        // Create rates for a wide date range
        for (var d = new DateOnly(2020, 1, 1); d <= new DateOnly(2026, 12, 31); d = d.AddDays(1))
        {
            dateRange[d] = 0; // placeholder, overwritten per pair
        }

        var rates = new Dictionary<string, SortedList<DateOnly, decimal>>(StringComparer.OrdinalIgnoreCase);

        // GBP/USD pair (inverted in FxConversionService: USD->GBP = 1/rate)
        // GBPUSD = 1.25 means £1 = $1.25, so $1 = £0.80
        rates["GBPUSD"] = MakeConstantRates(1.25m);

        // GBP/EUR pair (inverted: EUR->GBP = 1/rate)
        // GBPEUR = 1.16 means £1 = €1.16, so €1 = £0.862...
        rates["GBPEUR"] = MakeConstantRates(1.16m);

        // USDT/USD (not inverted)
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

    private static SortedList<DateOnly, decimal> MakeConstantRates(decimal rate)
    {
        var rates = new SortedList<DateOnly, decimal>();
        for (var d = new DateOnly(2020, 1, 1); d <= new DateOnly(2026, 12, 31); d = d.AddDays(30))
        {
            rates[d] = rate;
        }
        return rates;
    }

    private static Dictionary<string, SortedList<DateOnly, decimal>> GetRateCache(FxConversionService service)
    {
        var field = typeof(FxConversionService).GetField("_rateCache", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find _rateCache field on FxConversionService");
        return (Dictionary<string, SortedList<DateOnly, decimal>>)field.GetValue(service)!;
    }
}
