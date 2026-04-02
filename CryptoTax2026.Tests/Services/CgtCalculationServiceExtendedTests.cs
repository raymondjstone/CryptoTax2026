using System;
using System.Collections.Generic;
using System.Linq;
using CryptoTax2026.Models;
using CryptoTax2026.Services;
using CryptoTax2026.Tests.Helpers;
using Xunit;

namespace CryptoTax2026.Tests.Services;

/// <summary>
/// Tests for CGT features added after the initial test project:
/// delisting, cost overrides, loss carry-forward, spend/receive types,
/// crypto-denominated fees, staking net amounts, and RebuildSummariesOnly.
/// </summary>
public class CgtCalculationServiceExtendedTests
{
    // ========== Delisted Asset Filtering ==========

    [Fact]
    public void DelistedAsset_PostDelistingEntriesFiltered()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var delisted = new List<DelistedAssetEvent>
        {
            new() { Asset = "LUNA", ClaimType = "Negligible Value", DelistingDate = new DateTimeOffset(2023, 5, 15, 0, 0, 0, TimeSpan.Zero) }
        };
        var calc = new CgtCalculationService(fx, warnings, delistedAssets: delisted);

        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 5, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 500m, "LUNA", 100m) // Before delisting — kept
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "LUNA", 50m, "GBP", 100m) // After delisting — filtered
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        // Only the delisting disposal should exist (no manual sell after delisting date)
        var allDisposals = results.SelectMany(r => r.Disposals).ToList();
        Assert.DoesNotContain(allDisposals, d => d.DisposalProceeds == 100m);

        // Should have delisting warning
        Assert.Contains(warnings, w => w.Category == "Delisting" && w.Level == WarningLevel.Warning);
    }

    [Fact]
    public void DelistedAsset_SyntheticDisposalAtZeroProceeds()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var delisted = new List<DelistedAssetEvent>
        {
            new() { Asset = "LUNA", ClaimType = "Negligible Value", DelistingDate = new DateTimeOffset(2023, 7, 1, 0, 0, 0, TimeSpan.Zero) }
        };
        var calc = new CgtCalculationService(fx, warnings, delistedAssets: delisted);

        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 5, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 500m, "LUNA", 100m)
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        // The delisting should create a disposal with £0 proceeds
        var disposal = results.SelectMany(r => r.Disposals)
            .FirstOrDefault(d => d.Asset == "LUNA" && d.DisposalProceeds == 0m);
        Assert.NotNull(disposal);
        Assert.True(disposal.AllowableCost > 0); // Should have cost basis from purchase
        Assert.True(disposal.GainOrLoss < 0); // Should be a loss
    }

    [Fact]
    public void DelistedAsset_ZeroHolding_NoDisposal()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var delisted = new List<DelistedAssetEvent>
        {
            new() { Asset = "LUNA", ClaimType = "Negligible Value", DelistingDate = new DateTimeOffset(2023, 8, 1, 0, 0, 0, TimeSpan.Zero) }
        };
        var calc = new CgtCalculationService(fx, warnings, delistedAssets: delisted);

        // Buy then sell everything before delisting date
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 5, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 500m, "LUNA", 100m)
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "LUNA", 100m, "GBP", 200m)
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        // No delisting disposal should be created (already sold everything)
        var delistingDisposals = results.SelectMany(r => r.Disposals)
            .Where(d => d.DisposalProceeds == 0m && d.Asset == "LUNA")
            .ToList();
        Assert.Empty(delistingDisposals);

        Assert.Contains(warnings, w => w.Category == "Delisting" && w.Level == WarningLevel.Info
                                       && w.Message.Contains("no holding"));
    }

    // ========== Cost Basis Overrides ==========

    [Fact]
    public void CostBasisOverride_AppliedToSpecificTradeId()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);

        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m, refId: "TRADE-OVERRIDE")
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 1500m, refId: "TRADE-SELL")
            .Build();

        var overrides = new Dictionary<string, decimal> { ["TRADE-SELL"] = 1200m };
        var calc = new CgtCalculationService(fx, warnings, costBasisOverrides: overrides);

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals)
            .First(d => d.TradeId == "TRADE-SELL");
        Assert.Equal(1200m, disposal.AllowableCost);
        Assert.Equal(300m, disposal.GainOrLoss); // 1500 - 1200
    }

    // ========== Loss Carry-Forward ==========

    [Fact]
    public void LossCarryForward_LossesCarriedToNextYear()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Year 1 (2022/23): Buy ETH at £5000, sell at £2000 = £3000 loss
        // Year 2 (2023/24): Buy ETH at £1000, sell at £8000 = £7000 gain
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2022, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 5000m, "ETH", 1m)
            .AddTrade(
                new DateTimeOffset(2022, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 2000m) // £3000 loss
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m)
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 8000m) // £7000 gain
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var year1 = results.First(r => r.TaxYear == "2022/23");
        var year2 = results.First(r => r.TaxYear == "2023/24");

        // Year 1: net loss of £3000, carried out
        Assert.Equal(0m, year1.LossesCarriedIn);
        Assert.True(year1.LossesCarriedOut > 0);

        // Year 2: should have losses carried in
        Assert.True(year2.LossesCarriedIn > 0);
    }

    [Fact]
    public void LossCarryForward_LossesReduceGainToAeaNotBelow()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Year 1 (2023/24): Create a large loss (AEA = £6000 for 2023/24)
        // Year 2 (2024/25): Create a gain just above AEA (AEA = £3000 for 2024/25)
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 50000m, "BTC", 1m)
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "BTC", 1m, "GBP", 10000m) // £40000 loss
            .AddTrade(
                new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m)
            .AddTrade(
                new DateTimeOffset(2024, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 5000m) // £4000 gain
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var year2 = results.First(r => r.TaxYear == "2024/25");
        // Gain = £4000, AEA = £3000, excess above AEA = £1000
        // Carried-in losses should reduce excess to 0, but NOT below AEA
        Assert.Equal(0m, year2.TaxableGain);
        // Used losses should be £1000 (the excess above AEA), not more
        Assert.Equal(1000m, year2.LossesUsedThisYear);
    }

    // ========== Spend/Receive Trade Types ==========

    [Fact]
    public void SpendAndReceive_TreatedAsTrade()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Simulate a Kraken Instant Buy: spend GBP, receive ETH
        var refId = "INSTANT-BUY-1";
        var time = new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = refId, Time = time, Type = "spend",
                Asset = "ZGBP", AmountStr = "-2000", FeeStr = "0",
                LedgerId = "L-1", NormalisedAsset = "GBP"
            },
            new()
            {
                RefId = refId, Time = time, Type = "receive",
                Asset = "XETH", AmountStr = "1", FeeStr = "0",
                LedgerId = "L-2", NormalisedAsset = "ETH"
            }
        };

        // Now sell the ETH
        var builder = new LedgerBuilder();
        var sellLedger = builder.AddTrade(
            new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
            "ETH", 1m, "GBP", 2500m).Build();
        ledger.AddRange(sellLedger);

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).FirstOrDefault(d => d.Asset == "ETH");
        Assert.NotNull(disposal);
        Assert.Equal(2500m, disposal.DisposalProceeds);
        Assert.Equal(2000m, disposal.AllowableCost);
        Assert.Equal(500m, disposal.GainOrLoss);
    }

    // ========== Crypto-Denominated Fees ==========

    [Fact]
    public void CryptoFee_ReducesAcquiredQuantity()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH with 0.01 ETH fee — should acquire 0.99 ETH
        var refId = "FEE-TRADE-1";
        var time = new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = refId, Time = time, Type = "trade",
                Asset = "ZGBP", AmountStr = "-2000", FeeStr = "0",
                LedgerId = "L-1", NormalisedAsset = "GBP"
            },
            new()
            {
                RefId = refId, Time = time, Type = "trade",
                Asset = "XETH", AmountStr = "1", FeeStr = "0.01",
                LedgerId = "L-2", NormalisedAsset = "ETH"
            }
        };

        // Sell the net amount (0.99 ETH)
        var sellTime = new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-1", Time = sellTime, Type = "trade",
            Asset = "XETH", AmountStr = "-0.99", FeeStr = "0",
            LedgerId = "L-3", NormalisedAsset = "ETH"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-1", Time = sellTime, Type = "trade",
            Asset = "ZGBP", AmountStr = "2500", FeeStr = "0",
            LedgerId = "L-4", NormalisedAsset = "GBP"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).FirstOrDefault(d => d.Asset == "ETH");
        Assert.NotNull(disposal);
        // Pool should have 0.99 ETH, sell 0.99 — no over-removal
        Assert.Equal(0.99m, disposal.QuantityDisposed);
    }

    [Fact]
    public void CryptoFee_OnDisposal_IncreasesQuantityDisposed()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 2 ETH at £2000
        // Sell 1 ETH with 0.01 ETH fee — total leaving pool = 1.01 ETH
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 4000m, "ETH", 2m)
            .Build();

        var sellTime = new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-FEE", Time = sellTime, Type = "trade",
            Asset = "XETH", AmountStr = "-1", FeeStr = "0.01",
            LedgerId = "L-5", NormalisedAsset = "ETH"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-FEE", Time = sellTime, Type = "trade",
            Asset = "ZGBP", AmountStr = "2500", FeeStr = "0",
            LedgerId = "L-6", NormalisedAsset = "GBP"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).First(d => d.Asset == "ETH");
        // Disposed quantity should include the fee: 1 + 0.01 = 1.01
        Assert.Equal(1.01m, disposal.QuantityDisposed);
    }

    // ========== Staking Net Amount (Gross - Fee) ==========

    [Fact]
    public void StakingReward_NetOfFee()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Kraken charges 20% commission: gross=5 DOT, fee=1 DOT, net=4 DOT
        var time = new DateTimeOffset(2023, 8, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "STAKE-1", Time = time, Type = "staking",
                Asset = "DOT", AmountStr = "5", FeeStr = "1",
                LedgerId = "S-1", NormalisedAsset = "DOT"
            }
        };

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var summary = results[0];
        Assert.Single(summary.StakingRewards);
        var reward = summary.StakingRewards[0];
        Assert.Equal(4m, reward.Amount); // Net amount (gross 5 - fee 1)
        Assert.Equal(20m, reward.GbpValue); // 4 DOT * £5/DOT = £20 (net for income reporting)

        // But the CGT event (cost basis in S104 pool) uses net amount: 5 - 1 = 4 DOT
        // Verify by selling 4 DOT — should find exactly 4 in pool
        var sellLedger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 10, 1, 10, 0, 0, TimeSpan.Zero),
                "DOT", 4m, "GBP", 30m)
            .Build();
        ledger.AddRange(sellLedger);

        var results2 = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());
        var disposal = results2.SelectMany(r => r.Disposals).First(d => d.Asset == "DOT");
        Assert.Equal(4m, disposal.QuantityDisposed); // Net staking amount in pool
    }

    // ========== RebuildSummariesOnly ==========

    [Fact]
    public void RebuildSummariesOnly_ReturnsNullWithoutPriorCalculation()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var result = calc.RebuildSummariesOnly(new Dictionary<string, TaxYearUserInput>());
        Assert.Null(result);
    }

    [Fact]
    public void RebuildSummariesOnly_RecalculatesCgtWithNewInputs()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "BTC", 1m)
            .AddTrade(
                new DateTimeOffset(2024, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "BTC", 1m, "GBP", 14000m) // £13000 gain
            .Build();

        // First calculation with no income
        var inputs1 = new Dictionary<string, TaxYearUserInput>
        {
            ["2024/25"] = new TaxYearUserInput { TaxableIncome = 0m }
        };
        var results1 = calc.CalculateAllTaxYears(ledger, inputs1);
        var cgt1 = results1[0].CgtDue;

        // Rebuild with higher income (should push more gain into higher rate)
        var inputs2 = new Dictionary<string, TaxYearUserInput>
        {
            ["2024/25"] = new TaxYearUserInput { TaxableIncome = 60000m }
        };
        var results2 = calc.RebuildSummariesOnly(inputs2);
        Assert.NotNull(results2);
        var cgt2 = results2![0].CgtDue;

        // With higher income, more of the gain falls in the higher rate band
        Assert.True(cgt2 > cgt1);
    }

    // ========== Bed & Breakfast Boundary (30 vs 31 days) ==========

    [Fact]
    public void BedAndBreakfast_Day30IsMatched_Day31IsNot()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var disposalDate = new DateTimeOffset(2023, 7, 1, 10, 0, 0, TimeSpan.Zero);

        var ledger = new LedgerBuilder()
            // Initial purchase
            .AddTrade(
                new DateTimeOffset(2023, 5, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 2000m, "ETH", 2m)
            // Sell 2 ETH
            .AddTrade(disposalDate, "ETH", 2m, "GBP", 3000m)
            // Re-buy at day 30 (should be B&B)
            .AddTrade(disposalDate.AddDays(30), "GBP", 1200m, "ETH", 1m)
            // Re-buy at day 31 (should NOT be B&B)
            .AddTrade(disposalDate.AddDays(31), "GBP", 1300m, "ETH", 1m)
            .Build();

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposals = results.SelectMany(r => r.Disposals).ToList();
        var bnb = disposals.Where(d => d.MatchingRule == "Bed & Breakfast").ToList();
        var s104 = disposals.Where(d => d.MatchingRule == "Section 104").ToList();

        // 1 ETH matched by B&B (day 30), 1 ETH by Section 104
        Assert.Single(bnb);
        Assert.NotEmpty(s104);
    }

    // ========== FinalPools Exposed ==========

    [Fact]
    public void FinalPools_ReflectsRemainingHoldings()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 4000m, "ETH", 2m) // Buy 2 ETH at £2000 each
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 2500m) // Sell 1 ETH
            .Build();

        calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.True(calc.FinalPools.ContainsKey("ETH"));
        Assert.Equal(1m, calc.FinalPools["ETH"].Quantity);
        Assert.Equal(2000m, calc.FinalPools["ETH"].PooledCost);
    }

    // ========== Conversion/Adjustment (Token Migration) ==========

    [Fact]
    public void AdjustmentType_SameAsset_NotTreatedAsDisposal()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy POL, then simulate same-asset adjustment (should be ignored)
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 2000m, "POL", 500m)
            .Build();

        var adjTime = new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "ADJ-1", Time = adjTime, Type = "adjustment",
            Asset = "POL", AmountStr = "-500", FeeStr = "0",
            LedgerId = "A-1", NormalisedAsset = "POL"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "ADJ-1", Time = adjTime, Type = "adjustment",
            Asset = "POL", AmountStr = "500", FeeStr = "0",
            LedgerId = "A-2", NormalisedAsset = "POL"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        // No disposals should exist — same asset on both sides
        Assert.All(results, r => Assert.Empty(r.Disposals));
    }

    // ========== Deposit Establishes Cost Basis ==========

    [Fact]
    public void Deposit_EstablishesCostBasisAtMarketValue()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Deposit 1 BTC (market value £30,000), then sell for £35,000
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "DEP-1",
                Time = new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Type = "deposit", Asset = "BTC", AmountStr = "1", FeeStr = "0",
                LedgerId = "D-1", NormalisedAsset = "BTC"
            }
        };

        var sellLedger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "BTC", 1m, "GBP", 35000m)
            .Build();
        ledger.AddRange(sellLedger);

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).First(d => d.Asset == "BTC");
        Assert.Equal(35000m, disposal.DisposalProceeds);
        Assert.Equal(30000m, disposal.AllowableCost); // Market value at deposit
        Assert.Equal(5000m, disposal.GainOrLoss);
    }

    // ========== Airdrop/Fork Types ==========

    [Fact]
    public void AirdropType_TreatedAsStakingIncome()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var time = new DateTimeOffset(2023, 8, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "AIR-1", Time = time, Type = "airdrop",
                Asset = "DOT", AmountStr = "10", FeeStr = "0",
                LedgerId = "A-1", NormalisedAsset = "DOT"
            }
        };

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.Single(results);
        Assert.Single(results[0].StakingRewards);
        Assert.Equal(50m, results[0].StakingIncome); // 10 DOT * £5 = £50
    }

    // ========== Section 104 Pool History ==========

    [Fact]
    public void Section104Pool_HistoryTracksAcquisitions()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m)
            .AddTrade(
                new DateTimeOffset(2023, 8, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 3000m, "ETH", 1m)
            .Build();

        calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var pool = calc.FinalPools["ETH"];
        Assert.Equal(2, pool.History.Count);
        Assert.Equal(2m, pool.Quantity);
        Assert.Equal(4000m, pool.PooledCost);
    }

    // ========== CGT Higher Rate ==========

    [Fact]
    public void CgtDue_HigherRateWithHighIncome()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // 2024/25: AEA = £3000, basic rate = 10%, higher rate = 20%
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "BTC", 1m)
            .AddTrade(
                new DateTimeOffset(2024, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "BTC", 1m, "GBP", 14000m) // £13000 gain
            .Build();

        // Income well above basic rate band → all gains at higher rate
        var inputs = new Dictionary<string, TaxYearUserInput>
        {
            ["2024/25"] = new TaxYearUserInput { TaxableIncome = 100000m }
        };

        var results = calc.CalculateAllTaxYears(ledger, inputs);
        var summary = results[0];

        // Taxable gain: £13000 - £3000 AEA = £10000, all at higher rate (20%)
        Assert.Equal(10000m, summary.TaxableGain);
        Assert.Equal(2000m, summary.CgtDue);
    }

    // ========== Rounding Tolerance on Pool Shortfall ==========

    [Fact]
    public void TinyPoolShortfall_SnappedToPoolQuantity_NoWarning()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Simulate accumulated rounding: many small buys with crypto fees
        // that create a tiny shortfall when selling everything
        var builder = new LedgerBuilder();
        decimal totalNet = 0;

        // 10 buys of ~100 ETH each, with fees creating rounding
        for (int i = 0; i < 10; i++)
        {
            var date = new DateTimeOffset(2023, 5, 1 + i, 10, 0, 0, TimeSpan.Zero);
            builder.AddTrade(date, "GBP", 2000m, "ETH", 100m);
            totalNet += 100m;
        }

        // Sell slightly more than pool has (simulating rounding shortfall < 0.001)
        // The pool should have exactly 1000 ETH, but we sell 1000.0005
        var sellDate = new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var ledger = builder.Build();

        // Manually add a sell with a tiny excess
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-ALL", Time = sellDate.ToUnixTimeSeconds(), Type = "trade",
            Asset = "XETH", AmountStr = "-1000.0005", FeeStr = "0",
            LedgerId = "S-1", NormalisedAsset = "ETH"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-ALL", Time = sellDate.ToUnixTimeSeconds(), Type = "trade",
            Asset = "ZGBP", AmountStr = "25000", FeeStr = "0",
            LedgerId = "S-2", NormalisedAsset = "GBP"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        // Should NOT generate a pool shortfall warning for < 0.001 units
        Assert.DoesNotContain(warnings, w => w.Category == "Pool" && w.Level == WarningLevel.Warning);
    }

    [Fact]
    public void LargePoolShortfall_StillWarns()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH, sell 2 ETH — real shortfall of 1 unit
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 2000m, "ETH", 1m)
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 2m, "GBP", 4000m)
            .Build();

        calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        Assert.Contains(warnings, w => w.Category == "Pool" && w.Level == WarningLevel.Warning);
    }

    // ========== GBP Fee Direction (Buy vs Sell) ==========

    [Fact]
    public void GbpFeeOnBuy_IncreasesAcquisitionCost()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH for 1000 GBP with £10 GBP fee → total cost should be 1010
        var buyTime = new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "BUY-FEE", Time = buyTime, Type = "trade",
                Asset = "ZGBP", AmountStr = "-1000", FeeStr = "10",
                LedgerId = "L-1", NormalisedAsset = "GBP"
            },
            new()
            {
                RefId = "BUY-FEE", Time = buyTime, Type = "trade",
                Asset = "XETH", AmountStr = "1", FeeStr = "0",
                LedgerId = "L-2", NormalisedAsset = "ETH"
            }
        };

        // Sell 1 ETH for 1500 GBP (no fee) → gain should be 1500 - 1010 = 490
        var sellTime = new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-1", Time = sellTime, Type = "trade",
            Asset = "XETH", AmountStr = "-1", FeeStr = "0",
            LedgerId = "L-3", NormalisedAsset = "ETH"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-1", Time = sellTime, Type = "trade",
            Asset = "ZGBP", AmountStr = "1500", FeeStr = "0",
            LedgerId = "L-4", NormalisedAsset = "GBP"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).First(d => d.Asset == "ETH");
        Assert.Equal(1500m, disposal.DisposalProceeds);
        Assert.Equal(1010m, disposal.AllowableCost); // 1000 + 10 fee
        Assert.Equal(490m, disposal.GainOrLoss);
    }

    [Fact]
    public void GbpFeeOnSell_ReducesDisposalProceeds()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH for 1000 GBP (no fee)
        var ledger = new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero),
                "GBP", 1000m, "ETH", 1m)
            .Build();

        // Sell 1 ETH for 1500 GBP with £10 GBP fee → proceeds = 1490
        var sellTime = new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-FEE", Time = sellTime, Type = "trade",
            Asset = "XETH", AmountStr = "-1", FeeStr = "0",
            LedgerId = "L-5", NormalisedAsset = "ETH"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-FEE", Time = sellTime, Type = "trade",
            Asset = "ZGBP", AmountStr = "1500", FeeStr = "10",
            LedgerId = "L-6", NormalisedAsset = "GBP"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).First(d => d.Asset == "ETH");
        Assert.Equal(1490m, disposal.DisposalProceeds); // 1500 - 10 fee
        Assert.Equal(1000m, disposal.AllowableCost);
        Assert.Equal(490m, disposal.GainOrLoss);
    }

    [Fact]
    public void GbpFeeOnBothSides_BuyAndSell_CorrectGain()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH for 1000 GBP with £10 fee → cost = 1010
        var buyTime = new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "BUY-1", Time = buyTime, Type = "trade",
                Asset = "ZGBP", AmountStr = "-1000", FeeStr = "10",
                LedgerId = "L-1", NormalisedAsset = "GBP"
            },
            new()
            {
                RefId = "BUY-1", Time = buyTime, Type = "trade",
                Asset = "XETH", AmountStr = "1", FeeStr = "0",
                LedgerId = "L-2", NormalisedAsset = "ETH"
            }
        };

        // Sell 1 ETH for 1500 GBP with £15 fee → proceeds = 1485
        var sellTime = new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-1", Time = sellTime, Type = "trade",
            Asset = "XETH", AmountStr = "-1", FeeStr = "0",
            LedgerId = "L-3", NormalisedAsset = "ETH"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SELL-1", Time = sellTime, Type = "trade",
            Asset = "ZGBP", AmountStr = "1500", FeeStr = "15",
            LedgerId = "L-4", NormalisedAsset = "GBP"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).First(d => d.Asset == "ETH");
        Assert.Equal(1485m, disposal.DisposalProceeds); // 1500 - 15
        Assert.Equal(1010m, disposal.AllowableCost);    // 1000 + 10
        Assert.Equal(475m, disposal.GainOrLoss);
    }

    // ========== Fiat (USD/EUR) Fee Direction ==========

    [Fact]
    public void UsdFeeOnBuy_IncreasesAcquisitionCost()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH for 1250 USD with $10 USD fee
        // USD->GBP rate = 0.80, so total cost = (1250 + 10) * 0.80 = £1008
        var buyTime = new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "USD-BUY", Time = buyTime, Type = "trade",
                Asset = "ZUSD", AmountStr = "-1250", FeeStr = "10",
                LedgerId = "L-1", NormalisedAsset = "USD"
            },
            new()
            {
                RefId = "USD-BUY", Time = buyTime, Type = "trade",
                Asset = "XETH", AmountStr = "1", FeeStr = "0",
                LedgerId = "L-2", NormalisedAsset = "ETH"
            }
        };

        // Sell 1 ETH for 2000 GBP (no fee)
        ledger.AddRange(new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 2000m)
            .Build());

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).First(d => d.Asset == "ETH");
        Assert.Equal(2000m, disposal.DisposalProceeds);
        Assert.Equal(1008m, disposal.AllowableCost); // (1250 + 10) * 0.80
        Assert.Equal(992m, disposal.GainOrLoss);
    }

    // ========== Combined GBP + Crypto Fee ==========

    [Fact]
    public void GbpFeeAndCryptoFee_BothIncludedInCostBasis()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH for 1000 GBP with £10 GBP fee AND 0.01 ETH crypto fee
        // Cost basis = 1010 (GBP) + 20 (0.01 ETH * £2000) = 1030
        // Net acquired = 0.99 ETH
        var buyTime = new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "DUAL-FEE", Time = buyTime, Type = "trade",
                Asset = "ZGBP", AmountStr = "-1000", FeeStr = "10",
                LedgerId = "L-1", NormalisedAsset = "GBP"
            },
            new()
            {
                RefId = "DUAL-FEE", Time = buyTime, Type = "trade",
                Asset = "XETH", AmountStr = "1", FeeStr = "0.01",
                LedgerId = "L-2", NormalisedAsset = "ETH"
            }
        };

        calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var pool = calc.FinalPools["ETH"];
        Assert.Equal(0.99m, pool.Quantity);       // Net: 1 - 0.01 fee
        Assert.Equal(1030m, pool.PooledCost);      // 1010 GBP cost + 20 crypto fee value
    }

    // ========== Staking Income Net Reporting ==========

    [Fact]
    public void StakingIncome_ReportedNetOfCommission()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Two staking rewards:
        // 10 DOT gross, 2 DOT fee → 8 DOT net, £40 income
        // 5 DOT gross, 0 fee → 5 DOT net, £25 income
        var time1 = new DateTimeOffset(2023, 7, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var time2 = new DateTimeOffset(2023, 8, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "S-1", Time = time1, Type = "staking",
                Asset = "DOT", AmountStr = "10", FeeStr = "2",
                LedgerId = "S-1", NormalisedAsset = "DOT"
            },
            new()
            {
                RefId = "S-2", Time = time2, Type = "staking",
                Asset = "DOT", AmountStr = "5", FeeStr = "0",
                LedgerId = "S-2", NormalisedAsset = "DOT"
            }
        };

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var summary = results[0];
        Assert.Equal(2, summary.StakingRewards.Count);
        Assert.Equal(65m, summary.StakingIncome); // £40 + £25

        // Pool should have net tokens: 8 + 5 = 13
        Assert.Equal(13m, calc.FinalPools["DOT"].Quantity);
        Assert.Equal(65m, calc.FinalPools["DOT"].PooledCost); // 13 * £5
    }

    // ========== GBP Fee with Same-Day and B&B Matching ==========

    [Fact]
    public void GbpFee_CorrectWithSameDayMatching()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        var sameDay = new DateTimeOffset(2023, 7, 15, 0, 0, 0, TimeSpan.Zero);

        // Buy 1 ETH for 1000 GBP with £20 fee in the morning → cost = 1020
        var buyTime = sameDay.AddHours(9).ToUnixTimeSeconds();
        var ledger = new List<KrakenLedgerEntry>
        {
            new()
            {
                RefId = "SD-BUY", Time = buyTime, Type = "trade",
                Asset = "ZGBP", AmountStr = "-1000", FeeStr = "20",
                LedgerId = "L-1", NormalisedAsset = "GBP"
            },
            new()
            {
                RefId = "SD-BUY", Time = buyTime, Type = "trade",
                Asset = "XETH", AmountStr = "1", FeeStr = "0",
                LedgerId = "L-2", NormalisedAsset = "ETH"
            }
        };

        // Sell 1 ETH for 1500 GBP with £10 fee in the afternoon → proceeds = 1490
        var sellTime = sameDay.AddHours(15).ToUnixTimeSeconds();
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SD-SELL", Time = sellTime, Type = "trade",
            Asset = "XETH", AmountStr = "-1", FeeStr = "0",
            LedgerId = "L-3", NormalisedAsset = "ETH"
        });
        ledger.Add(new KrakenLedgerEntry
        {
            RefId = "SD-SELL", Time = sellTime, Type = "trade",
            Asset = "ZGBP", AmountStr = "1500", FeeStr = "10",
            LedgerId = "L-4", NormalisedAsset = "GBP"
        });

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        var disposal = results.SelectMany(r => r.Disposals).First(d => d.Asset == "ETH");
        Assert.Equal("Same Day", disposal.MatchingRule);
        Assert.Equal(1490m, disposal.DisposalProceeds);
        Assert.Equal(1020m, disposal.AllowableCost);
        Assert.Equal(470m, disposal.GainOrLoss);
    }

    // ========== Multiple Buys with GBP Fees — Pool Averaging ==========

    [Fact]
    public void GbpFees_AcrossMultipleBuys_PoolCostIncludesAllFees()
    {
        var warnings = new List<CalculationWarning>();
        var fx = TestFxHelper.CreateWithDefaultRates(warnings);
        var calc = new CgtCalculationService(fx, warnings);

        // Buy 1 ETH for 1000 GBP with £10 fee → cost 1010
        var buy1 = new DateTimeOffset(2023, 5, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        // Buy 1 ETH for 2000 GBP with £20 fee → cost 2020
        var buy2 = new DateTimeOffset(2023, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var ledger = new List<KrakenLedgerEntry>
        {
            new() { RefId = "B1", Time = buy1, Type = "trade", Asset = "ZGBP", AmountStr = "-1000", FeeStr = "10", LedgerId = "L-1", NormalisedAsset = "GBP" },
            new() { RefId = "B1", Time = buy1, Type = "trade", Asset = "XETH", AmountStr = "1", FeeStr = "0", LedgerId = "L-2", NormalisedAsset = "ETH" },
            new() { RefId = "B2", Time = buy2, Type = "trade", Asset = "ZGBP", AmountStr = "-2000", FeeStr = "20", LedgerId = "L-3", NormalisedAsset = "GBP" },
            new() { RefId = "B2", Time = buy2, Type = "trade", Asset = "XETH", AmountStr = "1", FeeStr = "0", LedgerId = "L-4", NormalisedAsset = "ETH" },
        };

        // Sell 1 ETH for 2500 GBP → pool avg cost = (1010 + 2020) / 2 = 1515
        ledger.AddRange(new LedgerBuilder()
            .AddTrade(
                new DateTimeOffset(2023, 9, 1, 10, 0, 0, TimeSpan.Zero),
                "ETH", 1m, "GBP", 2500m)
            .Build());

        var results = calc.CalculateAllTaxYears(ledger, new Dictionary<string, TaxYearUserInput>());

        // Pool should have 2 ETH at total cost 3030 before the sell
        var disposal = results.SelectMany(r => r.Disposals).First(d => d.Asset == "ETH");
        Assert.Equal(2500m, disposal.DisposalProceeds);
        Assert.Equal(1515m, disposal.AllowableCost); // 3030 / 2
        Assert.Equal(985m, disposal.GainOrLoss);

        // Remaining pool: 1 ETH at £1515
        Assert.Equal(1m, calc.FinalPools["ETH"].Quantity);
        Assert.Equal(1515m, calc.FinalPools["ETH"].PooledCost);
    }
}
