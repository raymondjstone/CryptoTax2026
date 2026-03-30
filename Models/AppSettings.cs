using System;
using System.Collections.Generic;

namespace CryptoTax2026.Models;

public class AppSettings
{
    public string KrakenApiKey { get; set; } = "";
    public string KrakenApiSecret { get; set; } = "";
    public Dictionary<string, TaxYearUserInput> TaxYearInputs { get; set; } = new();
    public List<DelistedAssetEvent> DelistedAssets { get; set; } = new();
    public List<CsvImportMapping> CsvMappings { get; set; } = new();
    public List<ManualLedgerEntry> ManualLedgerEntries { get; set; } = new();
    public Dictionary<string, string> DisposalNotes { get; set; } = new();
    public Dictionary<string, decimal> CostBasisOverrides { get; set; } = new(); // TradeId -> GBP cost
    public string Theme { get; set; } = "Default"; // "Default", "Light", "Dark"
    public string? CustomDataPath { get; set; } // null = default %LocalAppData%, set to OneDrive path for sync

    // Data freshness timestamps
    public DateTimeOffset? LastLedgerDownload { get; set; }
    public DateTimeOffset? LastFxDownload { get; set; }

    // Audit log
    public List<AuditLogEntry> AuditLog { get; set; } = new();

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
    public string ClaimType { get; set; } = "Delisted"; // "Delisted" or "Negligible Value"
}

/// <summary>
/// Saved column mapping for a CSV import profile (e.g. "Coinbase", "Binance").
/// </summary>
public class CsvImportMapping
{
    public string ProfileName { get; set; } = "";
    public string DateColumn { get; set; } = "";
    public string TypeColumn { get; set; } = "";        // buy/sell/trade
    public string AssetColumn { get; set; } = "";
    public string AmountColumn { get; set; } = "";
    public string FeeColumn { get; set; } = "";
    public string FeeAssetColumn { get; set; } = "";
    public string PriceColumn { get; set; } = "";
    public string QuoteCurrencyColumn { get; set; } = "";
    public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
    public bool HasHeader { get; set; } = true;
}

public class AuditLogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// A manually entered ledger entry from a CSV import or manual entry.
/// Stored in settings so it persists across sessions.
/// </summary>
public class ManualLedgerEntry
{
    public string Source { get; set; } = "";   // e.g. "Coinbase CSV", "Manual"
    public string RefId { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public string Type { get; set; } = "trade";
    public string Asset { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public string NormalisedAsset { get; set; } = "";
}
