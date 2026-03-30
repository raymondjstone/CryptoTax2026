using System;
using System.Collections.Generic;

namespace CryptoTax2026.Models;

public class TaxYearSummary
{
    public string TaxYear { get; set; } = ""; // e.g. "2023/24"
    public int StartYear { get; set; } // e.g. 2023 (April 6, 2023 to April 5, 2024)

    // User inputs
    public decimal TaxableIncome { get; set; }
    public decimal OtherCapitalGains { get; set; }

    // Calculated - CGT
    public List<DisposalRecord> Disposals { get; set; } = new();
    public decimal TotalDisposalProceeds { get; set; }
    public decimal TotalAllowableCosts { get; set; }
    public decimal TotalGains { get; set; }
    public decimal TotalLosses { get; set; }
    public decimal NetGainOrLoss => TotalGains + TotalLosses; // losses are negative
    public decimal AnnualExemptAmount { get; set; }
    public decimal TaxableGain { get; set; }
    public decimal CgtDue { get; set; }

    // Loss carry-forward
    public decimal LossesCarriedIn { get; set; }   // Unused losses from prior years
    public decimal LossesUsedThisYear { get; set; } // Portion of carried-in losses applied
    public decimal LossesCarriedOut { get; set; }   // Unused losses passed to next year

    // Tax rates used
    public decimal BasicRateCgt { get; set; }
    public decimal HigherRateCgt { get; set; }
    public decimal BasicRateBand { get; set; }
    public decimal PersonalAllowance { get; set; }

    // Staking income (taxed as miscellaneous income, separate from CGT)
    public decimal StakingIncome { get; set; }
    public List<StakingReward> StakingRewards { get; set; } = new();

    // Portfolio balance snapshots at tax year boundaries
    public BalanceSnapshot StartOfYearBalances { get; set; } = new();
    public BalanceSnapshot EndOfYearBalances { get; set; } = new();

    // Warnings and data issues
    public List<CalculationWarning> Warnings { get; set; } = new();
}

public class StakingReward
{
    public DateTimeOffset Date { get; set; }
    public string Asset { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal GbpValue { get; set; }

    public string DateFormatted => Date.ToString("dd/MM/yyyy");
}
