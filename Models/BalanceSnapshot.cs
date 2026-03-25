using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoTax2026.Models;

public class BalanceSnapshot
{
    public string Label { get; set; } = ""; // e.g. "Start of 2023/24" or "End of 2023/24"
    public DateTimeOffset Date { get; set; }
    public List<AssetBalance> Balances { get; set; } = new();
    public decimal TotalGbpValue => Balances.Sum(b => b.GbpValue);
}

public class AssetBalance
{
    public string Asset { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal GbpValue { get; set; }

    public string QuantityFormatted => Quantity.ToString("0.########");
    public string GbpFormatted => GbpValue < 0
        ? $"-£{System.Math.Abs(GbpValue):#,##0.00}"
        : $"£{GbpValue:#,##0.00}";
}
