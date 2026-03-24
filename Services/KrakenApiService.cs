using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

public class KrakenApiService
{
    private const string BaseUrl = "https://api.kraken.com";
    private const int BatchSize = 50; // Kraken returns max 50 trades per request
    private const int RateLimitDelayMs = 4000; // Kraken allows ~15 calls per minute for trade history
    private const int MaxRetries = 5;

    private readonly HttpClient _httpClient;
    private string _apiKey = "";
    private string _apiSecret = "";
    private long _lastNonce;

    public KrakenApiService()
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    private long GetNonce()
    {
        // Use high-precision ticks (100ns intervals since epoch) to guarantee the nonce
        // is higher than any previously used value, even from other applications.
        // Kraken requires a strictly increasing unsigned integer per API key.
        var nonce = DateTimeOffset.UtcNow.Ticks;
        if (nonce <= _lastNonce)
            nonce = _lastNonce + 1;
        _lastNonce = nonce;
        return nonce;
    }

    public void SetCredentials(string apiKey, string apiSecret)
    {
        _apiKey = apiKey;
        _apiSecret = apiSecret;
    }

    /// <summary>
    /// Tests the API connection by fetching the first page of trades.
    /// Returns the raw JSON response for debugging.
    /// </summary>
    public async Task<string> TestConnectionAsync()
    {
        var path = "/0/private/TradesHistory";
        var nonce = GetNonce().ToString();
        var postBody = $"nonce={nonce}&ofs=0";
        var signature = GenerateSignature(path, nonce, postBody);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(postBody, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.Add("API-Key", _apiKey);
        request.Headers.Add("API-Sign", signature);

        var response = await _httpClient.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Downloads trades from Kraken, resuming from the given timestamp.
    /// Pass 0 to download everything from the beginning.
    /// </summary>
    public async Task<List<KrakenTrade>> DownloadTradesAsync(
        double startFromUnixTime = 0,
        IProgress<(int count, string status)>? progress = null,
        CancellationToken ct = default)
    {
        var allTrades = new List<KrakenTrade>();
        int offset = 0;
        bool hasMore = true;

        var resumeLabel = startFromUnixTime > 0
            ? $" from {DateTimeOffset.FromUnixTimeSeconds((long)startFromUnixTime):dd MMM yyyy HH:mm}"
            : "";

        while (hasMore && !ct.IsCancellationRequested)
        {
            progress?.Report((allTrades.Count, $"Downloading trades{resumeLabel} (offset {offset})..."));

            List<KrakenTrade>? batch = null;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    batch = await GetTradesHistoryAsync(offset, ct, startFromUnixTime);
                    break; // success
                }
                catch (Exception ex) when (ex.Message.Contains("EAPI:Rate limit", StringComparison.OrdinalIgnoreCase)
                                         || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                {
                    var waitSeconds = 10 * (1 << attempt);
                    progress?.Report((allTrades.Count,
                        $"Rate limited. Waiting {waitSeconds}s before retry ({attempt + 1}/{MaxRetries})..."));
                    await Task.Delay(waitSeconds * 1000, ct);
                }
            }

            if (batch == null)
                throw new Exception("Rate limit exceeded after maximum retries. Try again later.");

            if (batch.Count == 0)
            {
                hasMore = false;
            }
            else
            {
                allTrades.AddRange(batch);
                offset += batch.Count;

                if (batch.Count < BatchSize)
                    hasMore = false;
            }

            if (hasMore)
                await Task.Delay(RateLimitDelayMs, ct);
        }

        progress?.Report((allTrades.Count, $"Download complete. {allTrades.Count} new trades found."));

        foreach (var trade in allTrades)
        {
            ParseAssetPair(trade);
        }

        return allTrades.OrderBy(t => t.Time).ToList();
    }

    private async Task<List<KrakenTrade>> GetTradesHistoryAsync(int offset, CancellationToken ct, double startTime = 0)
    {
        var path = "/0/private/TradesHistory";
        var nonce = GetNonce().ToString();

        // Build the post body string manually so it matches exactly what we sign
        var postBody = startTime > 0
            ? $"nonce={nonce}&ofs={offset}&start={startTime}"
            : $"nonce={nonce}&ofs={offset}";

        var signature = GenerateSignature(path, nonce, postBody);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(postBody, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.Add("API-Key", _apiKey);
        request.Headers.Add("API-Sign", signature);

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        var result = JsonSerializer.Deserialize<KrakenResponse>(json);
        if (result == null)
            throw new Exception("Failed to parse Kraken API response");

        if (result.Error != null && result.Error.Count > 0)
            throw new Exception($"Kraken API error: {string.Join(", ", result.Error)}");

        if (result.Result?.Trades == null)
            return new List<KrakenTrade>();

        var trades = new List<KrakenTrade>();
        foreach (var (tradeId, trade) in result.Result.Trades)
        {
            trade.TradeId = tradeId;
            trades.Add(trade);
        }

        return trades;
    }

    private string GenerateSignature(string path, string nonce, string postData)
    {
        var sha256 = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + postData));
        var pathBytes = Encoding.UTF8.GetBytes(path);

        var combined = new byte[pathBytes.Length + sha256.Length];
        Buffer.BlockCopy(pathBytes, 0, combined, 0, pathBytes.Length);
        Buffer.BlockCopy(sha256, 0, combined, pathBytes.Length, sha256.Length);

        var secretBytes = Convert.FromBase64String(_apiSecret);
        using var hmac = new HMACSHA512(secretBytes);
        var hash = hmac.ComputeHash(combined);

        return Convert.ToBase64String(hash);
    }

    private static void ParseAssetPair(KrakenTrade trade)
    {
        var pair = trade.Pair;

        // Kraken uses various pair formats. Common patterns:
        // XXBTZGBP -> XBT/GBP, XETHZGBP -> ETH/GBP
        // ADAGBP -> ADA/GBP, DOTGBP -> DOT/GBP
        // Also: XXBTZUSD, XETHZUSD etc.

        var knownQuotes = new[] { "ZGBP", "ZUSD", "ZEUR", "ZJPY", "GBP", "USD", "EUR", "USDT", "USDC" };
        var knownBases = new Dictionary<string, string>
        {
            ["XXBT"] = "BTC", ["XETH"] = "ETH", ["XXRP"] = "XRP",
            ["XLTC"] = "LTC", ["XXLM"] = "XLM", ["XXDG"] = "DOGE",
            ["XZEC"] = "ZEC", ["XMLN"] = "MLN", ["XXMR"] = "XMR",
            ["XREP"] = "REP", ["XETC"] = "ETC"
        };

        foreach (var quote in knownQuotes)
        {
            if (pair.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
            {
                var basePart = pair[..^quote.Length];
                trade.QuoteAsset = quote.TrimStart('Z');
                trade.BaseAsset = knownBases.TryGetValue(basePart, out var mapped) ? mapped : basePart;
                return;
            }
        }

        // Fallback: try splitting common 3+3 or 3+4 patterns
        if (pair.Length >= 6)
        {
            // Try 3-char quote first (GBP, USD, EUR)
            var possibleQuote = pair[^3..];
            if (possibleQuote is "GBP" or "USD" or "EUR")
            {
                trade.BaseAsset = pair[..^3];
                trade.QuoteAsset = possibleQuote;
                return;
            }

            // Try 4-char quote (USDT, USDC)
            if (pair.Length >= 7)
            {
                possibleQuote = pair[^4..];
                if (possibleQuote is "USDT" or "USDC")
                {
                    trade.BaseAsset = pair[..^4];
                    trade.QuoteAsset = possibleQuote;
                    return;
                }
            }
        }

        // Last resort
        trade.BaseAsset = pair;
        trade.QuoteAsset = "GBP";
    }
}

public class KrakenResponse
{
    [JsonPropertyName("error")]
    public List<string>? Error { get; set; }

    [JsonPropertyName("result")]
    public KrakenTradesResult? Result { get; set; }
}

public class KrakenTradesResult
{
    [JsonPropertyName("trades")]
    public Dictionary<string, KrakenTrade>? Trades { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}
