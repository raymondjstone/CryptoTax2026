using CryptoTax2026.Models;
using Xunit;

namespace CryptoTax2026.Tests.Models;

public class KrakenLedgerEntryTests
{
    [Theory]
    [InlineData("XXBT", "BTC")]
    [InlineData("XBT", "BTC")]
    [InlineData("XETH", "ETH")]
    [InlineData("XXRP", "XRP")]
    [InlineData("XLTC", "LTC")]
    [InlineData("XXDG", "DOGE")]
    [InlineData("ZGBP", "GBP")]
    [InlineData("ZUSD", "USD")]
    [InlineData("ZEUR", "EUR")]
    [InlineData("ZJPY", "JPY")]
    [InlineData("ZCAD", "CAD")]
    [InlineData("ZAUD", "AUD")]
    [InlineData("ETH2", "ETH")]
    [InlineData("ETH2.S", "ETH")]
    public void NormaliseAssetName_MapsKrakenNames(string krakenName, string expected)
    {
        Assert.Equal(expected, KrakenLedgerEntry.NormaliseAssetName(krakenName));
    }

    [Theory]
    [InlineData("DOT.S", "DOT")]
    [InlineData("ADA.S", "ADA")]
    [InlineData("SOL.S", "SOL")]
    public void NormaliseAssetName_RemovesStakedSuffix(string krakenName, string expected)
    {
        Assert.Equal(expected, KrakenLedgerEntry.NormaliseAssetName(krakenName));
    }

    [Theory]
    [InlineData("BTC", "BTC")]
    [InlineData("ETH", "ETH")]
    [InlineData("USDT", "USDT")]
    [InlineData("SOL", "SOL")]
    public void NormaliseAssetName_PassesThroughUnknownNames(string name, string expected)
    {
        Assert.Equal(expected, KrakenLedgerEntry.NormaliseAssetName(name));
    }

    [Fact]
    public void IsFiat_CorrectlyIdentifiesFiatCurrencies()
    {
        var fiatAssets = new[] { "GBP", "USD", "EUR", "JPY", "CAD", "AUD", "CHF" };
        foreach (var asset in fiatAssets)
        {
            var entry = new KrakenLedgerEntry { NormalisedAsset = asset };
            Assert.True(entry.IsFiat, $"{asset} should be fiat");
        }
    }

    [Fact]
    public void IsFiat_StablecoinsAreNotFiat()
    {
        var stablecoins = new[] { "USDT", "USDC", "DAI" };
        foreach (var asset in stablecoins)
        {
            var entry = new KrakenLedgerEntry { NormalisedAsset = asset };
            Assert.False(entry.IsFiat, $"{asset} should NOT be fiat");
        }
    }

    [Fact]
    public void IsFiat_CryptoIsNotFiat()
    {
        var crypto = new[] { "BTC", "ETH", "SOL", "DOT", "ADA" };
        foreach (var asset in crypto)
        {
            var entry = new KrakenLedgerEntry { NormalisedAsset = asset };
            Assert.False(entry.IsFiat, $"{asset} should NOT be fiat");
        }
    }

    [Fact]
    public void Amount_ParsesFromString()
    {
        var entry = new KrakenLedgerEntry { AmountStr = "1.23456789" };
        Assert.Equal(1.23456789m, entry.Amount);
    }

    [Fact]
    public void Amount_NegativeValuesWork()
    {
        var entry = new KrakenLedgerEntry { AmountStr = "-0.5" };
        Assert.Equal(-0.5m, entry.Amount);
    }

    [Fact]
    public void Amount_InvalidStringReturnsZero()
    {
        var entry = new KrakenLedgerEntry { AmountStr = "not_a_number" };
        Assert.Equal(0m, entry.Amount);
    }

    [Fact]
    public void Fee_ParsesFromString()
    {
        var entry = new KrakenLedgerEntry { FeeStr = "0.001" };
        Assert.Equal(0.001m, entry.Fee);
    }

    [Fact]
    public void DateTime_ConvertsFromUnixTime()
    {
        // 1700000000 = 2023-11-14T22:13:20Z
        var entry = new KrakenLedgerEntry { Time = 1700000000 };
        Assert.Equal(2023, entry.DateTime.Year);
        Assert.Equal(11, entry.DateTime.Month);
        Assert.Equal(14, entry.DateTime.Day);
    }
}
