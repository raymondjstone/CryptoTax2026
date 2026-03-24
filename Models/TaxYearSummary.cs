using System.Collections.Generic;

namespace CryptoTax2026.Models;

public class TaxYearSummary
{
    public string TaxYear { get; set; } = ""; // e.g. "2023/24"
    public int StartYear { get; set; } // e.g. 2023 (April 6, 2023 to April 5, 2024)

    // User inputs
    public decimal TaxableIncome { get; set; }
    public decimal OtherCapitalGains { get; set; }

    // Calculated
    public List<DisposalRecord> Disposals { get; set; } = new();
    public decimal TotalDisposalProceeds { get; set; }
    public decimal TotalAllowableCosts { get; set; }
    public decimal TotalGains { get; set; }
    public decimal TotalLosses { get; set; }
    public decimal NetGainOrLoss => TotalGains + TotalLosses; // losses are negative
    public decimal AnnualExemptAmount { get; set; }
    public decimal TaxableGain { get; set; }
    public decimal CgtDue { get; set; }

    // Tax rates used
    public decimal BasicRateCgt { get; set; }
    public decimal HigherRateCgt { get; set; }
    public decimal BasicRateBand { get; set; }
    public decimal PersonalAllowance { get; set; }
}
