using System;
using System.Text.Json.Serialization;

namespace CryptoTax2026.Models;

public class KrakenTrade
{
    [JsonPropertyName("ordertxid")]
    public string OrderTxId { get; set; } = "";

    [JsonPropertyName("pair")]
    public string Pair { get; set; } = "";

    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = ""; // "buy" or "sell"

    [JsonPropertyName("ordertype")]
    public string OrderType { get; set; } = "";

    [JsonPropertyName("price")]
    public string PriceStr { get; set; } = "0";

    [JsonPropertyName("cost")]
    public string CostStr { get; set; } = "0";

    [JsonPropertyName("fee")]
    public string FeeStr { get; set; } = "0";

    [JsonPropertyName("vol")]
    public string VolumeStr { get; set; } = "0";

    [JsonPropertyName("margin")]
    public string MarginStr { get; set; } = "0";

    [JsonPropertyName("misc")]
    public string Misc { get; set; } = "";

    // Computed properties
    [JsonIgnore] public decimal Price => decimal.TryParse(PriceStr, out var v) ? v : 0;
    [JsonIgnore] public decimal Cost => decimal.TryParse(CostStr, out var v) ? v : 0;
    [JsonIgnore] public decimal Fee => decimal.TryParse(FeeStr, out var v) ? v : 0;
    [JsonIgnore] public decimal Volume => decimal.TryParse(VolumeStr, out var v) ? v : 0;
    [JsonIgnore] public DateTimeOffset DateTime => DateTimeOffset.FromUnixTimeSeconds((long)Time);
    [JsonIgnore] public bool IsBuy => Type == "buy";
    [JsonIgnore] public bool IsSell => Type == "sell";

    // The trade ID assigned by us when storing
    public string TradeId { get; set; } = "";

    // Parsed asset pair
    public string BaseAsset { get; set; } = "";
    public string QuoteAsset { get; set; } = "";
}
