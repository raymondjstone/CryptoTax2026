using System;
using System.Collections.Generic;

namespace CryptoTax2026.Models;

public class AppSettings
{
    public string KrakenApiKey { get; set; } = "";
    public string KrakenApiSecret { get; set; } = "";
    public Dictionary<string, TaxYearUserInput> TaxYearInputs { get; set; } = new();
    public List<DelistedAssetEvent> DelistedAssets { get; set; } = new();

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

/// <summary>
/// Represents a manually-entered delisting event for an asset.
/// On the delisting date, the entire holding is treated as disposed at £0 proceeds.
/// Any ledger entries for this asset after the delisting date are ignored.
/// </summary>
public class DelistedAssetEvent
{
    public string Asset { get; set; } = "";
    public DateTimeOffset DelistingDate { get; set; }
    public string Notes { get; set; } = "";
}
