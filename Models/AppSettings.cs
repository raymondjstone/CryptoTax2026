using System.Collections.Generic;

namespace CryptoTax2026.Models;

public class AppSettings
{
    public string KrakenApiKey { get; set; } = "";
    public string KrakenApiSecret { get; set; } = "";
    public Dictionary<string, TaxYearUserInput> TaxYearInputs { get; set; } = new();
}

public class TaxYearUserInput
{
    public decimal TaxableIncome { get; set; }
    public decimal OtherCapitalGains { get; set; }
}
