using System;
using System.Collections.Generic;
using CryptoTax2026.Models;
using CryptoTax2026.Services;
using CryptoTax2026.Tests.Helpers;
using Xunit;

namespace CryptoTax2026.Tests.Services;

public class FxConversionServiceTests
{
    [Fact]
    public void ConvertToGbp_GbpReturnsIdentity()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);

        var result = fx.ConvertToGbp(100m, "GBP", DateTimeOffset.UtcNow);
        Assert.Equal(100m, result);
    }

    [Fact]
    public void ConvertToGbp_ZeroAmountReturnsZero()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);

        var result = fx.ConvertToGbp(0m, "BTC", DateTimeOffset.UtcNow);
        Assert.Equal(0m, result);
    }

    [Fact]
    public void ConvertToGbp_UsdConvertsCorrectly()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);

        // GBPUSD = 1.25, so USD->GBP = 1/1.25 = 0.80
        var result = fx.ConvertToGbp(100m, "USD", new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(80m, result);
    }

    [Fact]
    public void ConvertToGbp_EurConvertsCorrectly()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);

        // GBPEUR = 1.16, so EUR->GBP = 1/1.16 ≈ 0.8621
        var result = fx.ConvertToGbp(116m, "EUR", new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(100m, result); // 116 * (1/1.16) = 100
    }

    [Fact]
    public void ConvertToGbp_UsdtUsesTwoStepConversion()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);

        // USDT->USD = 1.00, then USD->GBP = 0.80
        var result = fx.ConvertToGbp(100m, "USDT", new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(80m, result); // 100 USDT * 1.00 USD/USDT * 0.80 GBP/USD = 80
    }

    [Fact]
    public void ConvertToGbp_UsdtIsNotSameAsUsd()
    {
        // Verify that USDT with a non-1.0 rate gives different result than USD
        var warnings = new List<CalculationWarning>();
        var rates = new Dictionary<string, SortedList<long, decimal>>(StringComparer.OrdinalIgnoreCase);

        var ts = new DateTimeOffset(2023, 6, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        rates["GBPUSD"] = new SortedList<long, decimal> { [ts] = 1.25m };  // USD->GBP = 0.80
        rates["USDTUSD"] = new SortedList<long, decimal> { [ts] = 0.98m }; // USDT is slightly depegged

        var pairMap = new Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)>
        {
            [("USD",  "GBP")] = ("GBPUSD",  true),
            [("USDT", "USD")] = ("USDTUSD", false),
        };

        var fx = TestFxHelper.CreateWithRates(warnings, rates, pairMap);
        var date = new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var usdResult = fx.ConvertToGbp(100m, "USD", date);
        var usdtResult = fx.ConvertToGbp(100m, "USDT", date);

        Assert.Equal(80m, usdResult);     // 100 * (1/1.25) = 80
        Assert.Equal(78.4m, usdtResult);  // 100 * 0.98 * (1/1.25) = 78.4
        Assert.NotEqual(usdResult, usdtResult);
    }

    [Fact]
    public void ConvertToGbp_BtcUsesDirectGbpPair()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);

        var result = fx.ConvertToGbp(0.5m, "BTC", new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(15000m, result); // 0.5 * 30000
    }

    [Fact]
    public void ConvertToGbp_UnknownAsset_ReturnsZeroNotRawAmount()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);

        // An unknown asset should return 0, NOT the raw amount (which would be wildly wrong)
        var result = fx.ConvertToGbp(1000m, "UNKNOWNCOIN", new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(0m, result);
        Assert.Contains(warnings, w => w.Level == WarningLevel.Error && w.Category == "FX Rate");
    }

    [Fact]
    public void GetGbpValueOfAsset_DelegatesToConvertToGbp()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var date = new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var direct = fx.ConvertToGbp(2m, "ETH", date);
        var viaHelper = fx.GetGbpValueOfAsset("ETH", 2m, date);

        Assert.Equal(direct, viaHelper);
        Assert.Equal(4000m, viaHelper); // 2 * 2000
    }

    // ========== FindClosestRate edge cases ==========

    [Fact]
    public void ConvertToGbp_DateBeforeCacheStart_UsesEarliestAvailableRate()
    {
        // Regression test: if a transaction pre-dates the cached rate data, FindClosestRate
        // must fall back to values[0] (earliest candle) rather than returning 0.
        var warnings = new List<CalculationWarning>();
        var cacheStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var rates = new Dictionary<string, SortedList<long, decimal>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GBPUSD"]   = new() { [cacheStart] = 1.25m },
            ["XXBTZGBP"] = new() { [cacheStart] = 30000m },
        };
        var pairMap = new Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)>
        {
            [("USD", "GBP")] = ("GBPUSD",   true),
            [("BTC", "GBP")] = ("XXBTZGBP", false),
        };

        var fx = TestFxHelper.CreateWithRates(warnings, rates, pairMap);

        // Request a date well before the cache starts — should use the earliest candle, not zero
        var dateBefore = new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var result = fx.ConvertToGbp(1m, "BTC", dateBefore);

        Assert.Equal(30000m, result);
        Assert.DoesNotContain(warnings, w => w.Level == WarningLevel.Error && w.Category == "FX Rate");
    }

    [Fact]
    public void ConvertToGbp_DateAfterCacheEnd_UsesLatestAvailableRate()
    {
        var warnings = new List<CalculationWarning>();
        var cacheEnd = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var rates = new Dictionary<string, SortedList<long, decimal>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GBPUSD"]   = new() { [cacheEnd] = 1.25m },
            ["XXBTZGBP"] = new() { [cacheEnd] = 45000m },
        };
        var pairMap = new Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)>
        {
            [("USD", "GBP")] = ("GBPUSD",   true),
            [("BTC", "GBP")] = ("XXBTZGBP", false),
        };

        var fx = TestFxHelper.CreateWithRates(warnings, rates, pairMap);

        // Request a date after the cache ends — should use the latest (only) candle
        var dateAfter = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var result = fx.ConvertToGbp(2m, "BTC", dateAfter);

        Assert.Equal(90000m, result); // 2 * 45000
        Assert.DoesNotContain(warnings, w => w.Level == WarningLevel.Error && w.Category == "FX Rate");
    }

    [Fact]
    public void ConvertToGbp_CryptoViaUsdFallback_ConvertsCorrectly()
    {
        // Asset has no GBP pair — must route via USD pair + GBPUSD
        var warnings = new List<CalculationWarning>();
        var ts = new DateTimeOffset(2023, 6, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var rates = new Dictionary<string, SortedList<long, decimal>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GBPUSD"] = new() { [ts] = 1.25m },  // USD→GBP = 1/1.25 = 0.80
            ["XRPUSD"] = new() { [ts] = 0.60m },  // XRP→USD = 0.60
        };
        var pairMap = new Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)>
        {
            [("USD", "GBP")] = ("GBPUSD", true),
            [("XRP", "USD")] = ("XRPUSD", false),
        };

        var fx = TestFxHelper.CreateWithRates(warnings, rates, pairMap);
        var date = new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // 100 XRP * $0.60 = $60 USD; $60 / 1.25 = £48
        var result = fx.ConvertToGbp(100m, "XRP", date);
        Assert.Equal(48m, result);
    }

    [Fact]
    public void ConvertToGbp_FxRateTypeAffectsResult_WhenOhlcSpreadDiffers()
    {
        // Candle: Open=900, High=1100, Low=900, Close=1000 → Average=(1100+900)/2=1000
        var ts = new DateTimeOffset(2023, 6, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var candle = new OhlcCandle { Timestamp = ts, Open = 900m, High = 1100m, Low = 900m, Close = 1000m };

        var ohlcRates = new Dictionary<string, SortedList<long, OhlcCandle>>(StringComparer.OrdinalIgnoreCase)
        {
            ["XXBTZGBP"] = new() { [ts] = candle },
        };
        var pairMap = new Dictionary<(string Asset, string Quote), (string CacheKey, bool Invert)>
        {
            [("BTC", "GBP")] = ("XXBTZGBP", false),
        };

        var date = new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero);

        decimal Convert(FxRateType rt) =>
            TestFxHelper.CreateWithOhlcRates(new(), rt, ohlcRates, pairMap)
                        .ConvertToGbp(1m, "BTC", date);

        Assert.Equal(900m,  Convert(FxRateType.Open));
        Assert.Equal(1100m, Convert(FxRateType.High));
        Assert.Equal(900m,  Convert(FxRateType.Low));
        Assert.Equal(1000m, Convert(FxRateType.Close));
        Assert.Equal(1000m, Convert(FxRateType.Average)); // (1100+900)/2 = 1000
    }
}
