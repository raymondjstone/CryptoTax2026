using System;
using System.Collections.Generic;
using System.Linq;
using CryptoTax2026.Models;
using CryptoTax2026.Services;
using CryptoTax2026.Tests.Helpers;
using Xunit;

namespace CryptoTax2026.Tests.Services;

public class BalanceSnapshotTests
{
    private static List<TaxYearSummary> Calculate(List<KrakenLedgerEntry> ledger)
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var svc = new CgtCalculationService(fx, warnings);
        return svc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());
    }

    [Fact]
    public void BalanceSnapshots_EmptyLedger_NoSnapshots()
    {
        var summaries = Calculate(new List<KrakenLedgerEntry>());
        Assert.Empty(summaries);
    }

    [Fact]
    public void BalanceSnapshots_SingleBuy_ShowsEndBalance()
    {
        // Buy 1 ETH on 15 June 2023 (tax year 2023/24)
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero),
                "GBP", 2000m,
                "ETH", 1m)
            .Build();

        var summaries = Calculate(ledger);
        var ty = summaries.First(s => s.TaxYear == "2023/24");

        // Start of year (6 Apr 2023) should have no ETH
        Assert.Empty(ty.StartOfYearBalances.Balances.Where(b => b.Asset == "ETH"));

        // End of year (5 Apr 2024) should have 1 ETH
        var endEth = ty.EndOfYearBalances.Balances.FirstOrDefault(b => b.Asset == "ETH");
        Assert.NotNull(endEth);
        Assert.Equal(1m, endEth.Quantity);
        Assert.True(endEth.GbpValue > 0);
    }

    [Fact]
    public void BalanceSnapshots_BuyAndSell_ShowsCorrectBalances()
    {
        // Buy 2 ETH in May 2023, sell 1 ETH in Jan 2024 (both in tax year 2023/24)
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 5, 15, 12, 0, 0, TimeSpan.Zero),
                "GBP", 4000m,
                "ETH", 2m)
            .AddTrade(
                new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
                "ETH", 1m,
                "GBP", 2500m)
            .Build();

        var summaries = Calculate(ledger);
        var ty = summaries.First(s => s.TaxYear == "2023/24");

        // End of year should have 1 ETH remaining
        var endEth = ty.EndOfYearBalances.Balances.FirstOrDefault(b => b.Asset == "ETH");
        Assert.NotNull(endEth);
        Assert.Equal(1m, endEth.Quantity);
    }

    [Fact]
    public void BalanceSnapshots_CrossTaxYear_CarriesForward()
    {
        // Buy 1 BTC in tax year 2022/23, check it appears in start of 2023/24
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2022, 7, 1, 12, 0, 0, TimeSpan.Zero),
                "GBP", 30000m,
                "BTC", 1m)
            // Add a staking entry in 2023/24 to ensure that year is in summaries
            .AddStaking(
                new DateTimeOffset(2023, 7, 1, 12, 0, 0, TimeSpan.Zero),
                "ETH", 0.01m)
            .Build();

        var summaries = Calculate(ledger);
        var ty2324 = summaries.FirstOrDefault(s => s.TaxYear == "2023/24");
        Assert.NotNull(ty2324);

        // Start of 2023/24 should have 1 BTC carried from previous year
        var startBtc = ty2324.StartOfYearBalances.Balances.FirstOrDefault(b => b.Asset == "BTC");
        Assert.NotNull(startBtc);
        Assert.Equal(1m, startBtc.Quantity);
    }

    [Fact]
    public void BalanceSnapshots_ExcludesFiat()
    {
        // Trade GBP for ETH — GBP should not appear in balance snapshots
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero),
                "GBP", 2000m,
                "ETH", 1m)
            .Build();

        var summaries = Calculate(ledger);
        var ty = summaries.First(s => s.TaxYear == "2023/24");

        Assert.DoesNotContain(ty.EndOfYearBalances.Balances, b => b.Asset == "GBP");
    }

    [Fact]
    public void BalanceSnapshots_TotalGbpValue_SumsCorrectly()
    {
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero),
                "GBP", 4000m,
                "ETH", 2m)
            .Build();

        var summaries = Calculate(ledger);
        var ty = summaries.First(s => s.TaxYear == "2023/24");

        // Total GBP value should equal sum of individual balances
        var total = ty.EndOfYearBalances.TotalGbpValue;
        var sum = ty.EndOfYearBalances.Balances.Sum(b => b.GbpValue);
        Assert.Equal(sum, total);
        Assert.True(total > 0);
    }

    [Fact]
    public void BalanceSnapshots_StakingReward_IncreasesBalance()
    {
        // Buy 1 ETH, then receive 0.05 ETH staking reward
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 5, 1, 12, 0, 0, TimeSpan.Zero),
                "GBP", 2000m,
                "ETH", 1m)
            .AddStaking(
                new DateTimeOffset(2023, 8, 1, 12, 0, 0, TimeSpan.Zero),
                "ETH", 0.05m)
            .Build();

        var summaries = Calculate(ledger);
        var ty = summaries.First(s => s.TaxYear == "2023/24");

        var endEth = ty.EndOfYearBalances.Balances.FirstOrDefault(b => b.Asset == "ETH");
        Assert.NotNull(endEth);
        Assert.Equal(1.05m, endEth.Quantity);
    }
}
