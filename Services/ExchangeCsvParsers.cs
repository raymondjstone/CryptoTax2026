using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

/// <summary>
/// Built-in CSV parsers for popular exchanges.
/// Each parser knows the exact column layout and date format for its exchange.
/// </summary>
public static class ExchangeCsvParsers
{
    public static readonly Dictionary<string, ExchangeProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Coinbase"] = new ExchangeProfile
        {
            Name = "Coinbase",
            Description = "Coinbase transaction history CSV (Account → Statements → Generate)",
            DateColumn = "Timestamp",
            DateFormats = new[] { "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-dd HH:mm:ss UTC", "yyyy-MM-ddTHH:mm:ss.fffZ" },
            TypeColumn = "Transaction Type",
            AssetColumn = "Asset",
            AmountColumn = "Quantity Transacted",
            FeeColumn = "Fees and/or Spread",
            PriceColumn = "Spot Price at Transaction",
            QuoteCurrencyColumn = "Spot Price Currency",
            HasHeader = true,
            TypeMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Buy"] = "trade",
                ["Sell"] = "trade",
                ["Send"] = "transfer",
                ["Receive"] = "transfer",
                ["Convert"] = "trade",
                ["Staking Income"] = "staking",
                ["Rewards Income"] = "staking",
                ["Learning Reward"] = "staking",
                ["Coinbase Earn"] = "staking",
                ["Advanced Trade Buy"] = "trade",
                ["Advanced Trade Sell"] = "trade",
            },
            AmountSignFromType = true, // Coinbase amounts are always positive; sign from Buy/Sell
            SellTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Sell", "Send", "Advanced Trade Sell" }
        },

        ["Binance"] = new ExchangeProfile
        {
            Name = "Binance",
            Description = "Binance transaction history CSV (Orders → Trade History → Export)",
            DateColumn = "Date(UTC)",
            DateFormats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" },
            TypeColumn = "Side",
            AssetColumn = "Market",
            AmountColumn = "Amount",
            FeeColumn = "Fee",
            FeeAssetColumn = "Fee Coin",
            PriceColumn = "Price",
            HasHeader = true,
            TypeMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BUY"] = "trade",
                ["SELL"] = "trade",
            },
            NeedsMarketPairSplit = true, // "Market" column is like "BTCUSDT" — need to split
            AmountSignFromType = true,
            SellTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SELL" }
        },

        ["Crypto.com"] = new ExchangeProfile
        {
            Name = "Crypto.com",
            Description = "Crypto.com App transaction history CSV",
            DateColumn = "Timestamp (UTC)",
            DateFormats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fffZ" },
            TypeColumn = "Transaction Kind",
            AssetColumn = "Currency",
            AmountColumn = "Amount",
            FeeColumn = "",
            PriceColumn = "Native Amount",
            QuoteCurrencyColumn = "Native Currency",
            HasHeader = true,
            TypeMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["crypto_purchase"] = "trade",
                ["crypto_exchange"] = "trade",
                ["viban_purchase"] = "trade",
                ["dust_conversion_credited"] = "trade",
                ["dust_conversion_debited"] = "trade",
                ["crypto_withdrawal"] = "transfer",
                ["crypto_deposit"] = "transfer",
                ["referral_card_cashback"] = "staking",
                ["rewards_platform_deposit_credited"] = "staking",
                ["crypto_earn_interest_paid"] = "staking",
                ["mco_stake_reward"] = "staking",
                ["supercharger_reward_to_app_credited"] = "staking",
                ["reimbursement"] = "staking",
                ["admin_wallet_credited"] = "staking",
            },
            AmountSignFromType = false, // Amount already has correct sign
        },

        ["Bybit"] = new ExchangeProfile
        {
            Name = "Bybit",
            Description = "Bybit trade history CSV (Assets → Spot → Order History → Export)",
            DateColumn = "Date",
            DateFormats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" },
            TypeColumn = "Side",
            AssetColumn = "Symbol",
            AmountColumn = "Qty",
            FeeColumn = "Fee",
            PriceColumn = "Price",
            HasHeader = true,
            TypeMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Buy"] = "trade",
                ["Sell"] = "trade",
            },
            NeedsMarketPairSplit = true,
            AmountSignFromType = true,
            SellTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Sell" }
        }
    };

    /// <summary>
    /// Parses a CSV file using a built-in exchange profile.
    /// Returns parsed entries and an error count.
    /// </summary>
    public static (List<ManualLedgerEntry> Entries, int Errors) Parse(
        string[] lines, ExchangeProfile profile, string sourceName)
    {
        var entries = new List<ManualLedgerEntry>();
        int errors = 0;

        if (lines.Length == 0) return (entries, 0);

        // Skip BOM and find header
        var headerLine = lines[0].TrimStart('\uFEFF');
        var headers = ParseCsvLine(headerLine);
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
            headerMap[headers[i].Trim()] = i;

        int dateIdx = FindColumn(headerMap, profile.DateColumn);
        int typeIdx = FindColumn(headerMap, profile.TypeColumn);
        int assetIdx = FindColumn(headerMap, profile.AssetColumn);
        int amountIdx = FindColumn(headerMap, profile.AmountColumn);
        int feeIdx = FindColumn(headerMap, profile.FeeColumn);
        int feeAssetIdx = FindColumn(headerMap, profile.FeeAssetColumn);
        int priceIdx = FindColumn(headerMap, profile.PriceColumn);
        int quoteCurrIdx = FindColumn(headerMap, profile.QuoteCurrencyColumn);

        if (dateIdx < 0 || amountIdx < 0)
            return (entries, lines.Length - 1);

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            var fields = ParseCsvLine(lines[i]);
            try
            {
                var dateStr = GetField(fields, dateIdx);
                if (string.IsNullOrEmpty(dateStr)) { errors++; continue; }

                DateTimeOffset date = default;
                bool parsed = false;
                foreach (var fmt in profile.DateFormats)
                {
                    if (DateTimeOffset.TryParseExact(dateStr.Trim(), fmt,
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
                    { parsed = true; break; }
                }
                if (!parsed && !DateTimeOffset.TryParse(dateStr.Trim(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
                { errors++; continue; }

                var rawType = GetField(fields, typeIdx);
                var type = "trade";
                if (!string.IsNullOrEmpty(rawType) && profile.TypeMapping.TryGetValue(rawType.Trim(), out var mapped))
                    type = mapped;

                var assetRaw = GetField(fields, assetIdx)?.Trim().ToUpperInvariant() ?? "";
                // For market pair columns (e.g. "BTCUSDT"), take the base asset
                if (profile.NeedsMarketPairSplit && assetRaw.Length > 3)
                {
                    // Try common quote suffixes
                    foreach (var quote in new[] { "USDT", "USDC", "BUSD", "USD", "GBP", "EUR", "BTC", "ETH", "BNB" })
                    {
                        if (assetRaw.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                        {
                            assetRaw = assetRaw[..^quote.Length];
                            break;
                        }
                    }
                }

                if (!decimal.TryParse(GetField(fields, amountIdx)?.Trim(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                { errors++; continue; }

                // Apply sign based on type if needed
                if (profile.AmountSignFromType && profile.SellTypes != null &&
                    !string.IsNullOrEmpty(rawType) && profile.SellTypes.Contains(rawType.Trim()))
                {
                    amount = -Math.Abs(amount);
                }

                decimal fee = 0;
                if (feeIdx >= 0)
                    decimal.TryParse(GetField(fields, feeIdx)?.Trim(),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out fee);

                var normalised = KrakenLedgerEntry.NormaliseAssetName(assetRaw);
                var refId = $"{sourceName}-{i}";

                entries.Add(new ManualLedgerEntry
                {
                    Source = sourceName,
                    RefId = refId,
                    Date = date,
                    Type = type,
                    Asset = assetRaw,
                    Amount = amount,
                    Fee = Math.Abs(fee),
                    NormalisedAsset = normalised
                });
            }
            catch
            {
                errors++;
            }
        }

        return (entries, errors);
    }

    private static int FindColumn(Dictionary<string, int> headerMap, string? columnName)
    {
        if (string.IsNullOrEmpty(columnName)) return -1;
        return headerMap.TryGetValue(columnName, out var idx) ? idx : -1;
    }

    private static string? GetField(string[] fields, int idx)
    {
        return idx >= 0 && idx < fields.Length ? fields[idx] : null;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}

public class ExchangeProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string DateColumn { get; set; } = "";
    public string[] DateFormats { get; set; } = Array.Empty<string>();
    public string TypeColumn { get; set; } = "";
    public string AssetColumn { get; set; } = "";
    public string AmountColumn { get; set; } = "";
    public string FeeColumn { get; set; } = "";
    public string FeeAssetColumn { get; set; } = "";
    public string PriceColumn { get; set; } = "";
    public string QuoteCurrencyColumn { get; set; } = "";
    public bool HasHeader { get; set; } = true;
    public Dictionary<string, string> TypeMapping { get; set; } = new();
    public bool NeedsMarketPairSplit { get; set; }
    public bool AmountSignFromType { get; set; }
    public HashSet<string>? SellTypes { get; set; }
}
