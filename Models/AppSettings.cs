using System.Collections.Generic;

namespace CryptoTax2026.Models;

public class AppSettings
{
    public string KrakenApiKey { get; set; } = "";
    public string KrakenApiSecret { get; set; } = "";
    public Dictionary<string, TaxYearUserInput> TaxYearInputs { get; set; } = new();

    // Window position and size
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool IsMaximized { get; set; }
}

public class TaxYearUserInput
{
    public decimal TaxableIncome { get; set; }
    public decimal OtherCapitalGains { get; set; }
}
