using System;
using System.Linq;
using CryptoTax2026.Services;
using Xunit;

namespace CryptoTax2026.Tests.Services;

public class ExchangeCsvParserTests
{
    // ========== Coinbase ==========

    [Fact]
    public void Coinbase_ParsesBuyRow()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "2023-06-15T12:00:00Z,Buy,ETH,1.5,2000.00,GBP,3000.00,3015.00,15.00,Bought 1.5 ETH"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal("trade", entry.Type);
        Assert.Equal("ETH", entry.Asset);
        Assert.Equal(1.5m, entry.Amount); // Buy = positive
        Assert.Equal(15m, entry.Fee);
        Assert.Equal("ETH", entry.NormalisedAsset);
    }

    [Fact]
    public void Coinbase_ParsesSellRow_NegativeAmount()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "2023-06-15T12:00:00Z,Sell,BTC,0.5,30000.00,GBP,15000.00,14985.00,15.00,Sold 0.5 BTC"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Single(entries);
        Assert.Equal(-0.5m, entries[0].Amount); // Sell = negative
    }

    [Fact]
    public void Coinbase_StakingIncome_MappedCorrectly()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "2023-06-15T12:00:00Z,Staking Income,ETH,0.01,2000.00,GBP,20.00,20.00,0.00,Staking reward"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Single(entries);
        Assert.Equal("staking", entries[0].Type);
    }

    // ========== Binance ==========

    [Fact]
    public void Binance_MarketPairSplit_ExtractsBaseAsset()
    {
        var profile = ExchangeCsvParsers.Profiles["Binance"];
        var lines = new[]
        {
            "Date(UTC),Market,Side,Price,Amount,Total,Fee,Fee Coin",
            "2023-06-15 12:00:00,BTCUSDT,BUY,30000.00,0.5,15000.00,15.00,USDT"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Single(entries);
        Assert.Equal("BTC", entries[0].Asset); // Extracted from BTCUSDT
        Assert.Equal("BTC", entries[0].NormalisedAsset);
    }

    [Fact]
    public void Binance_SellSide_NegativeAmount()
    {
        var profile = ExchangeCsvParsers.Profiles["Binance"];
        var lines = new[]
        {
            "Date(UTC),Market,Side,Price,Amount,Total,Fee,Fee Coin",
            "2023-06-15 12:00:00,ETHUSDT,SELL,2000.00,1.0,2000.00,2.00,USDT"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Equal(-1.0m, entries[0].Amount);
    }

    [Fact]
    public void Binance_MarketPairSplit_ETHBTC()
    {
        var profile = ExchangeCsvParsers.Profiles["Binance"];
        var lines = new[]
        {
            "Date(UTC),Market,Side,Price,Amount,Total,Fee,Fee Coin",
            "2023-06-15 12:00:00,ETHBTC,BUY,0.06,10.0,0.6,0.01,BTC"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Equal("ETH", entries[0].Asset); // BTC suffix removed
    }

    // ========== Error Handling ==========

    [Fact]
    public void Parse_EmptyFile_ReturnsEmpty()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var (entries, errors) = ExchangeCsvParsers.Parse(Array.Empty<string>(), profile, "test");

        Assert.Empty(entries);
        Assert.Equal(0, errors);
    }

    [Fact]
    public void Parse_InvalidDate_IncrementErrors()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "NOT-A-DATE,Buy,ETH,1.0,2000.00,GBP,2000.00,2015.00,15.00,Test"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Empty(entries);
        Assert.Equal(1, errors);
    }

    [Fact]
    public void Parse_InvalidAmount_IncrementErrors()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "2023-06-15T12:00:00Z,Buy,ETH,NOT_A_NUMBER,2000.00,GBP,2000.00,2015.00,15.00,Test"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Empty(entries);
        Assert.Equal(1, errors);
    }

    [Fact]
    public void Parse_MissingDateColumn_ReturnsAllErrors()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Wrong,Headers,Here",
            "2023-06-15,Buy,ETH"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Empty(entries);
        Assert.Equal(1, errors); // All data rows are errors
    }

    [Fact]
    public void Parse_QuotedFieldsWithCommas()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "2023-06-15T12:00:00Z,Buy,ETH,1.0,2000.00,GBP,2000.00,2015.00,15.00,\"Bought, for test\""
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Single(entries);
    }

    [Fact]
    public void Parse_BOM_HandledCorrectly()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "\uFEFFTimestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "2023-06-15T12:00:00Z,Buy,ETH,1.0,2000.00,GBP,2000.00,2015.00,15.00,Test"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Single(entries);
    }

    [Fact]
    public void Parse_SkipsBlankLines()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "",
            "2023-06-15T12:00:00Z,Buy,ETH,1.0,2000.00,GBP,2000.00,2015.00,15.00,Test",
            "   ",
            "2023-06-16T12:00:00Z,Buy,BTC,0.5,30000.00,GBP,15000.00,15015.00,15.00,Test2"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal(0, errors);
        Assert.Equal(2, entries.Count);
    }

    // ========== RefId Generation ==========

    [Fact]
    public void Parse_GeneratesUniqueRefIds()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "2023-06-15T12:00:00Z,Buy,ETH,1.0,2000.00,GBP,2000.00,2015.00,15.00,Test1",
            "2023-06-16T12:00:00Z,Buy,BTC,0.5,30000.00,GBP,15000.00,15015.00,15.00,Test2"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "myfile");

        Assert.Equal(2, entries.Count);
        Assert.NotEqual(entries[0].RefId, entries[1].RefId);
        Assert.StartsWith("myfile-", entries[0].RefId);
    }

    // ========== Asset Normalisation in Parsing ==========

    [Fact]
    public void Parse_NormalisesAssetNames()
    {
        var profile = ExchangeCsvParsers.Profiles["Coinbase"];
        var lines = new[]
        {
            "Timestamp,Transaction Type,Asset,Quantity Transacted,Spot Price at Transaction,Spot Price Currency,Subtotal,Total (inclusive of fees and/or spread),Fees and/or Spread,Notes",
            "2023-06-15T12:00:00Z,Buy,ETH2,1.0,2000.00,GBP,2000.00,2015.00,15.00,Test"
        };

        var (entries, errors) = ExchangeCsvParsers.Parse(lines, profile, "test");

        Assert.Equal("ETH2", entries[0].Asset); // Raw preserved
        Assert.Equal("ETH", entries[0].NormalisedAsset); // Normalised
    }

    // ========== Multiple Profiles Exist ==========

    [Fact]
    public void Profiles_ContainAllExpectedExchanges()
    {
        Assert.True(ExchangeCsvParsers.Profiles.ContainsKey("Coinbase"));
        Assert.True(ExchangeCsvParsers.Profiles.ContainsKey("Binance"));
        Assert.True(ExchangeCsvParsers.Profiles.ContainsKey("Crypto.com"));
        Assert.True(ExchangeCsvParsers.Profiles.ContainsKey("Bybit"));
    }
}
