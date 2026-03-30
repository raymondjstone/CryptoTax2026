using System;

namespace CryptoTax2026.Models;

public class DisposalRecord
{
    public string Asset { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public decimal QuantityDisposed { get; set; }
    public decimal DisposalProceeds { get; set; } // in GBP
    public decimal AllowableCost { get; set; }    // in GBP
    public decimal GainOrLoss => DisposalProceeds - AllowableCost;
    public string MatchingRule { get; set; } = ""; // "Same Day", "Bed & Breakfast", "Section 104"
    public string TradeId { get; set; } = "";
    public string AcquisitionRefId { get; set; } = ""; // RefId of the matched acquisition (B&B rule only)
    public string TaxYear { get; set; } = ""; // e.g. "2023/24"
}
