using System;
using CryptoTax2026.Models;
using CryptoTax2026.Services;
using Xunit;

namespace CryptoTax2026.Tests.Models;

public class OhlcCandleTests
{
    private static OhlcCandle MakeCandle() => new()
    {
        Timestamp = 1_700_000_000L,
        Open  = 100m,
        High  = 110m,
        Low   = 90m,
        Close = 105m,
    };

    [Fact]
    public void GetRate_Open_ReturnsOpen()
        => Assert.Equal(100m, MakeCandle().GetRate(FxRateType.Open));

    [Fact]
    public void GetRate_High_ReturnsHigh()
        => Assert.Equal(110m, MakeCandle().GetRate(FxRateType.High));

    [Fact]
    public void GetRate_Low_ReturnsLow()
        => Assert.Equal(90m, MakeCandle().GetRate(FxRateType.Low));

    [Fact]
    public void GetRate_Close_ReturnsClose()
        => Assert.Equal(105m, MakeCandle().GetRate(FxRateType.Close));

    [Fact]
    public void GetRate_Average_ReturnsHighPlusLowDividedByTwo()
        // (110 + 90) / 2 = 100
        => Assert.Equal(100m, MakeCandle().GetRate(FxRateType.Average));

    [Fact]
    public void GetRate_UnknownType_FallsBackToClose()
        => Assert.Equal(105m, MakeCandle().GetRate((FxRateType)999));

    [Fact]
    public void DateTime_ReturnsDateTimeOffsetFromTimestamp()
    {
        var candle   = new OhlcCandle { Timestamp = 1_700_000_000L };
        var expected = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000L);
        Assert.Equal(expected, candle.DateTime);
    }
}
