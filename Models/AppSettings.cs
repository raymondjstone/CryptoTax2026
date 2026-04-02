using System;
using System.Collections.Generic;
using System.Linq;

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
    public FxRateType FxRateType { get; set; } = FxRateType.Average; // HMRC compliant rate type

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
    public bool BuyMeCoffeeClicked { get; set; } = false;
    public DateTimeOffset? LastCoffeePrompt { get; set; }
    public DateTimeOffset? FirstAppUse { get; set; } // Track when the app was first used

    /// <summary>
    /// When true, entries sourced from the Kraken pair-events database (Notes = "Kraken")
    /// are excluded from CGT calculations. Only manually configured or explicitly-imported
    /// entries (non-Kraken Notes) are used.
    /// </summary>
    public bool IgnoreAutoDelistings { get; set; } = false;

    /// <summary>
    /// The effective list of delisted asset events passed to the CGT engine.
    /// When <see cref="IgnoreAutoDelistings"/> is true, entries with Notes = "Kraken"
    /// (auto-sourced from the bundled pair-events database) are excluded.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<DelistedAssetEvent> EffectiveDelistedAssets =>
        IgnoreAutoDelistings
            ? DelistedAssets.Where(e => !string.Equals(e.Notes, "Kraken", StringComparison.OrdinalIgnoreCase)).ToList()
            : DelistedAssets;
}

public class TaxYearUserInput
{
    public decimal TaxableIncome { get; set; }
    public decimal OtherCapitalGains { get; set; }
}

/// <summary>
/// Represents a delist (and optional relist) event for a specific Kraken trading pair.
/// On the delist date the entire holding of the underlying asset is treated as disposed at £0.
/// Post-delist ledger entries for that asset are ignored until the relist date (if any).
/// </summary>
public class DelistedAssetEvent
{
    /// <summary>The trading pair, e.g. "LUNAUSD" or "XXBTZGBP".</summary>
    public string Pair { get; set; } = "";

    /// <summary>
    /// Backward-compat field from the pre-pair-based format (v1 settings stored just the
    /// asset ticker here, e.g. "LUNA"). Populated from <see cref="Pair"/> since v2.
    /// </summary>
    public string Asset { get; set; } = "";

    public DateTimeOffset DelistingDate { get; set; }

    /// <summary>When set, the pair was relisted on this date and entries after it are valid.</summary>
    public DateTimeOffset? RelistDate { get; set; }

    public string Notes { get; set; } = "";
    public string ClaimType { get; set; } = "Delisted"; // "Delisted" or "Negligible Value"

    /// <summary>
    /// The base asset extracted from <see cref="Pair"/> (e.g. "LUNAUSD" → "LUNA").
    /// Falls back to the legacy <see cref="Asset"/> field when <see cref="Pair"/> is empty.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveAsset =>
        !string.IsNullOrWhiteSpace(Pair) ? ExtractBaseAsset(Pair) : Asset;

    // Quote currencies ordered longest-first so "USDT" is tried before "USD" etc.
    private static readonly string[] KnownQuotes =
    {
        "USDT", "USDC", "ZGBP", "ZUSD", "ZEUR", "ZJPY", "ZCAD", "ZAUD",
        "GBP", "USD", "EUR", "JPY", "CAD", "AUD", "CHF", "DAI"
    };

    /// <summary>
    /// Strips the known quote currency from the end of a Kraken pair name and normalises
    /// the resulting base ticker.  E.g. "LUNAUSD" → "LUNA", "XXBTZGBP" → "BTC".
    /// Returns the normalised input unchanged if no known quote is found.
    /// </summary>
    public static string ExtractBaseAsset(string pair)
    {
        var upper = pair.ToUpperInvariant().Trim();
        foreach (var quote in KnownQuotes)
        {
            if (upper.Length > quote.Length && upper.EndsWith(quote))
                return KrakenLedgerEntry.NormaliseAssetName(upper[..^quote.Length]);
        }
        return KrakenLedgerEntry.NormaliseAssetName(upper);
    }
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

/// <summary>
/// HMRC-compliant exchange rate calculation methods for cryptocurrency valuations.
/// These methods determine which price from daily OHLC data is used for tax calculations.
/// All rates are sourced from daily candles (24-hour periods) with 00:00:00 timestamps representing end-of-day positions.
/// </summary>
public enum FxRateType
{
    /// <summary>Daily average price - calculated as (High + Low) / 2 (default)</summary>
    Average,

    /// <summary>Daily opening price - first price of the trading day</summary>
    Open,

    /// <summary>Daily high price - highest price during the trading day</summary>
    High,

    /// <summary>Daily low price - lowest price during the trading day</summary>
    Low,

    /// <summary>Daily closing price - last price of the trading day</summary>
    Close
}
