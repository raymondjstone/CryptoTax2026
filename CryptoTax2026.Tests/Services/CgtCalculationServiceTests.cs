using System;
using System.Collections.Generic;
using System.Linq;
using CryptoTax2026.Models;
using CryptoTax2026.Services;
using CryptoTax2026.Tests.Helpers;
using Xunit;

namespace CryptoTax2026.Tests.Services;

public class CgtCalculationServiceTests
{
    // ========== Tax Year Label Tests ==========

    [Fact]
    public void GetTaxYearLabel_MidYear_ReturnsCorrectLabel()
    {
        var date = new DateTimeOffset(2023, 9, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("2023/24", CgtCalculationService.GetTaxYearLabel(date));
    }

    [Fact]
    public void GetTaxYearLabel_April5_BelongsToPreviousYear()
    {
        var date = new DateTimeOffset(2024, 4, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("2023/24", CgtCalculationService.GetTaxYearLabel(date));
    }

    [Fact]
    public void GetTaxYearLabel_April6_BelongsToNewYear()
    {
        var date = new DateTimeOffset(2024, 4, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("2024/25", CgtCalculationService.GetTaxYearLabel(date));
    }

    [Fact]
    public void GetTaxYearLabel_January_BelongsToPreviousStartYear()
    {
        var date = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("2023/24", CgtCalculationService.GetTaxYearLabel(date));
    }

    // ========== Simple Buy and Sell (Section 104) ==========

    [Fact]
    public void SimpleBuyAndSell_CalculatesGainCorrectly()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH for £1000 GBP, then sell for £1500 GBP
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m,   // spent 1000 GBP
                "ETH", 1m)      // received 1 ETH
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m,      // spent 1 ETH
                "GBP", 1500m)   // received 1500 GBP
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.Single(results);
        var summary = results[0];
        Assert.Equal("2023/24", summary.TaxYear);
        Assert.Single(summary.Disposals);

        var disposal = summary.Disposals[0];
        Assert.Equal("ETH", disposal.Asset);
        Assert.Equal(1500m, disposal.DisposalProceeds);
        Assert.Equal(1000m, disposal.AllowableCost);
        Assert.Equal(500m, disposal.GainOrLoss);
    }

    [Fact]
    public void SimpleBuyAndSell_LossCalculatedCorrectly()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH for £2000, sell for £1200 = loss of £800
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 2000m, "ETH", 1m)
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 1200m)
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results[0].Disposals[0];
        Assert.Equal(-800m, disposal.GainOrLoss);
    }

    // ========== Same-Day Rule ==========

    [Fact]
    public void SameDayRule_MatchesBuyAndSellOnSameDay()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var sameDay = new DateTimeOffset(2023, 7, 15, 0, 0, 0, TimeSpan.Zero);

        // Buy 2 ETH at £500 each in the morning, sell 1 ETH at £600 in the afternoon
        var ledger = new LedgerBuilder()
            .AddTrade(sameDay.AddHours(9), "GBP", 1000m, "ETH", 2m)
            .AddTrade(sameDay.AddHours(15), "ETH", 1m, "GBP", 600m)
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposals = results[0].Disposals;
        Assert.Contains(disposals, d => d.MatchingRule == "Same Day");
    }

    // ========== Bed & Breakfast Rule ==========

    [Fact]
    public void BedAndBreakfast_MatchesWithin30Days()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH, sell 1 ETH, buy 1 ETH again within 30 days
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m)  // Buy at £1000
            .AddTrade(
                new DateTimeOffset(2023, 7, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 1500m)  // Sell at £1500
            .AddTrade(
                new DateTimeOffset(2023, 7, 15, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1200m, "ETH", 1m)  // Re-buy within 30 days at £1200
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposals = results[0].Disposals;
        // The B&B rule should match the sale on 1 Jul with the re-purchase on 15 Jul
        Assert.Contains(disposals, d => d.MatchingRule == "Bed & Breakfast");
    }

    // ========== Section 104 Pool ==========

    [Fact]
    public void Section104_AveragesCostAcrossMultiplePurchases()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 1, 15, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m)  // Buy 1 ETH at £1000
            .AddTrade(
                new DateTimeOffset(2023, 3, 15, 10, 0, 0, TimeSpan.Zero),
                "GBP", 3000m, "ETH", 1m)  // Buy 1 ETH at £3000
            // Pool: 2 ETH, cost £4000, avg £2000/ETH
            .AddTrade(
                new DateTimeOffset(2023, 8, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 2500m)  // Sell 1 ETH at £2500
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var year = results.First(r => r.TaxYear == "2023/24");
        var s104 = year.Disposals.FirstOrDefault(d => d.MatchingRule == "Section 104");
        Assert.NotNull(s104);
        Assert.Equal(2500m, s104.DisposalProceeds);
        Assert.Equal(2000m, s104.AllowableCost); // Average cost £2000
        Assert.Equal(500m, s104.GainOrLoss);
    }

    // ========== Transfer Entries Are Skipped ==========

    [Fact]
    public void TransferEntries_AreNotTreatedAsDisposals()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH, then transfer between spot and staking (should NOT create disposal)
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 2000m, "ETH", 1m)
            .Build();

        // Add transfer entries (spot to staking) — these should be ignored
        var transferTime = new DateTimeOffset(2023, 7, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "TRANSFER-1",
            Time = transferTime,
            Type = "transfer",
            SubType = "spottostaking",
            Asset = "ETH",
            AmountStr = "-1.0",
            FeeStr = "0",
            LedgerId = "T-1",
            NormalisedAsset = "ETH"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "TRANSFER-2",
            Time = transferTime,
            Type = "transfer",
            SubType = "stakingfromspot",
            Asset = "ETH.S",
            AmountStr = "1.0",
            FeeStr = "0",
            LedgerId = "T-2",
            NormalisedAsset = "ETH"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        // Should have no disposals — only the acquisition from the trade
        foreach (var summary in results)
        {
            Assert.Empty(summary.Disposals);
        }
    }

    [Fact]
    public void FlexStakingTrade_SameNormalisedAsset_NotTreatedAsDisposal()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 2000m, "ETH", 1m)
            .Build();

        // Simulate flex staking "trade": -1 ETH, +1 XETH.F (both normalise to ETH)
        var flexTime = new DateTimeOffset(2023, 7, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "FLEX-1",
            Time = flexTime,
            Type = "trade",
            Asset = "XETH",
            AmountStr = "-1.0",
            FeeStr = "0",
            LedgerId = "F-1",
            NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName("XETH") // ETH
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "FLEX-1",
            Time = flexTime,
            Type = "trade",
            Asset = "XETH.F",
            AmountStr = "1.0",
            FeeStr = "0",
            LedgerId = "F-2",
            NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName("XETH.F") // ETH
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        // Should have no disposals — the flex staking "trade" should be skipped
        foreach (var summary in results)
        {
            Assert.Empty(summary.Disposals);
        }
    }

    // ========== Staking Rewards ==========

    [Fact]
    public void StakingRewards_TrackedAsMiscIncome()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var ledger = new LedgerBuilder()
            .AddStaking(
                new DateTimeOffset(2023, 8, 1, 10, 0, 0, TimeSpan.Zero),
                "DOT", 5m)  // 5 DOT staking reward
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.Single(results);
        var summary = results[0];
        Assert.Equal("2023/24", summary.TaxYear);
        Assert.Single(summary.StakingRewards);
        Assert.Equal(25m, summary.StakingIncome); // 5 DOT * £5 = £25
    }

    [Fact]
    public void StakingRewards_EstablishCostBasis()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Receive 10 DOT as staking reward (cost basis = market value at time)
        // Then sell 10 DOT at a higher price
        var ledger = new LedgerBuilder()
            .AddStaking(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "DOT", 10m)  // 10 DOT reward at £5 each = £50 cost basis
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "DOT", 10m, "GBP", 80m)  // Sell 10 DOT for £80
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var summary = results[0];
        var disposal = summary.Disposals[0];
        Assert.Equal(80m, disposal.DisposalProceeds);
        Assert.Equal(50m, disposal.AllowableCost); // Cost basis from staking reward
        Assert.Equal(30m, disposal.GainOrLoss);
    }

    [Fact]
    public void DividendType_TreatedSameAsStaking()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var ledger = new LedgerBuilder()
            .AddDividend(
                new DateTimeOffset(2023, 8, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 0.01m)
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.Single(results);
        Assert.Single(results[0].StakingRewards);
        Assert.True(results[0].StakingIncome > 0);
    }

    // ========== CGT Calculation ==========

    [Fact]
    public void CgtDue_ZeroWhenBelowAea()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Small gain below AEA
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m)
            .AddTrade(
                new DateTimeOffset(2024, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 2000m)  // £1000 gain, below AEA of £3000 for 2024/25
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.Equal(0m, results[0].CgtDue);
        Assert.Equal(0m, results[0].TaxableGain);
    }

    [Fact]
    public void CgtDue_BasicRateWithLowIncome()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Gain above AEA with basic rate income
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "BTC", 1m)
            .AddTrade(
                new DateTimeOffset(2024, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "BTC", 1m, "GBP", 14000m)  // £13000 gain
            .Build();

        var inputs = new Dictionary<string, TaxYearUserInput>
        {
            ["2024/25"] = new TaxYearUserInput { TaxableIncome = 20000m }
        };

        var results = calc.CalculateAllTaxYears(ledger, inputs);
        var summary = results[0];

        // Gain: £13000, AEA: £3000, Taxable: £10000
        // Income: 20000, PA: 12570, above PA: 7430, unused basic band: 37700 - 7430 = 30270
        // All gain at basic rate (10%): £10000 * 0.10 = £1000
        Assert.Equal(10000m, summary.TaxableGain);
        Assert.Equal(1000m, summary.CgtDue);
    }

    // ========== Deposit Warnings ==========

    [Fact]
    public void Deposits_GenerateInfoWarning()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var ledger = new LedgerBuilder()
            .AddDeposit(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m)
            .Build();

        calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.Contains(warnings, w => w.Category == "Deposit" && w.Level == WarningLevel.Info);
    }

    // ========== Multiple Tax Years ==========

    [Fact]
    public void MultipleYears_GroupedCorrectly()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m)
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 0.5m, "GBP", 800m)  // Sell half in 2023/24
            .AddTrade(
                new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 0.5m, "GBP", 900m)  // Sell rest in 2024/25
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.TaxYear == "2023/24");
        Assert.Contains(results, r => r.TaxYear == "2024/25");
    }
}
