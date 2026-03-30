using System;
using System.Text.Json.Serialization;

namespace CryptoTax2026.Models;

public class KrakenLedgerEntry
{
    private static readonly System.Collections.Generic.Dictionary<string, string> AssetMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XXBT"] = "BTC", ["XBT"] = "BTC",
        ["XETH"] = "ETH", ["XLTC"] = "LTC",
        ["XXRP"] = "XRP", ["XXLM"] = "XLM",
        ["XXDG"] = "DOGE", ["XZEC"] = "ZEC",
        ["XMLN"] = "MLN", ["XXMR"] = "XMR",
        ["XREP"] = "REP", ["XETC"] = "ETC",
        ["ZGBP"] = "GBP", ["ZUSD"] = "USD",
        ["ZEUR"] = "EUR", ["ZJPY"] = "JPY",
        ["ZCAD"] = "CAD", ["ZAUD"] = "AUD",
        // Staked variants
        ["ETH2"] = "ETH", ["ETH2.S"] = "ETH",
    };

    [JsonPropertyName("refid")]
    public string RefId { get; set; } = "";

    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = ""; // trade, deposit, withdrawal, staking, transfer, margin, rollover, spend, receive, settled, adjustment, sale, dividend

    [JsonPropertyName("subtype")]
    public string SubType { get; set; } = "";

    [JsonPropertyName("aclass")]
    public string AssetClass { get; set; } = "";

    [JsonPropertyName("asset")]
    public string Asset { get; set; } = "";

    [JsonPropertyName("amount")]
    public string AmountStr { get; set; } = "0";

    [JsonPropertyName("fee")]
    public string FeeStr { get; set; } = "0";

    [JsonPropertyName("balance")]
    public string BalanceStr { get; set; } = "0";

    // The ledger entry ID assigned by Kraken
    public string LedgerId { get; set; } = "";

    // Computed properties
    [JsonIgnore] public decimal Amount => decimal.TryParse(AmountStr, out var v) ? v : 0;
    [JsonIgnore] public decimal Fee => decimal.TryParse(FeeStr, out var v) ? v : 0;
    [JsonIgnore] public decimal Balance => decimal.TryParse(BalanceStr, out var v) ? v : 0;
    [JsonIgnore] public DateTimeOffset DateTime => DateTimeOffset.FromUnixTimeSeconds((long)Time);

    // Normalised asset name (Kraken uses XXBT for BTC, XETH for ETH, ZGBP for GBP etc.)
    public string NormalisedAsset { get; set; } = "";

    public bool IsFiat => NormalisedAsset is "GBP" or "USD" or "EUR" or "JPY" or "CAD" or "AUD" or "CHF";

    public static string NormaliseAssetName(string asset)
    {
        // Kraken prefixes: X for crypto, Z for fiat (legacy), plus some special names
        if (AssetMap.TryGetValue(asset, out var mapped))
            return mapped;

        // Handle Kraken suffixes for staking/bonding/margin/parachain variants:
        // .S = staked, .F = flex staking, .B = bonded staking, .M = margin, .P = parachain
        // e.g. "DOT.S" -> "DOT", "ETH.F" -> "ETH", "DOT.B" -> "DOT", "KSM.P" -> "KSM"
        if (asset.Length > 2 && asset[^2] == '.')
        {
            var suffix = char.ToUpperInvariant(asset[^1]);
            if (suffix is 'S' or 'F' or 'B' or 'M' or 'P')
            {
                var baseAsset = asset[..^2];
                // Recursively normalise in case the base also needs mapping (e.g., "XETH.F")
                return NormaliseAssetName(baseAsset);
            }
        }

        return asset;
    }
}
