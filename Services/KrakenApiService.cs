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
    /// Tests the API connection by fetching the first page of ledger entries.
    /// Returns the raw JSON response for debugging.
    /// </summary>
    public async Task<string> TestConnectionAsync()
    {
        var path = "/0/private/Ledgers";
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

    // ========== LEDGER DOWNLOAD ==========

    /// <summary>
    /// Downloads full ledger from Kraken, resuming from the given timestamp.
    /// The ledger contains all account activity: trades, staking, deposits, withdrawals, etc.
    /// </summary>
    public async Task<List<KrakenLedgerEntry>> DownloadLedgerAsync(
        double startFromUnixTime = 0,
        IProgress<(int count, string status)>? progress = null,
        CancellationToken ct = default)
    {
        var allEntries = new List<KrakenLedgerEntry>();
        int offset = 0;
        bool hasMore = true;

        var resumeLabel = startFromUnixTime > 0
            ? $" from {DateTimeOffset.FromUnixTimeSeconds((long)startFromUnixTime):dd MMM yyyy HH:mm}"
            : "";

        while (hasMore && !ct.IsCancellationRequested)
        {
            progress?.Report((allEntries.Count, $"Downloading ledger{resumeLabel} (offset {offset})..."));

            List<KrakenLedgerEntry>? batch = null;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    batch = await GetLedgerBatchAsync(offset, ct, startFromUnixTime);
                    break;
                }
                catch (Exception ex) when (ex.Message.Contains("EAPI:Rate limit", StringComparison.OrdinalIgnoreCase)
                                         || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                {
                    var waitSeconds = 10 * (1 << attempt);
                    progress?.Report((allEntries.Count,
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
                allEntries.AddRange(batch);
                offset += batch.Count;

                if (batch.Count < BatchSize)
                    hasMore = false;
            }

            if (hasMore)
                await Task.Delay(RateLimitDelayMs, ct);
        }

        progress?.Report((allEntries.Count, $"Download complete. {allEntries.Count} ledger entries found."));

        // Normalise asset names
        foreach (var entry in allEntries)
        {
            entry.NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName(entry.Asset);
        }

        return allEntries.OrderBy(e => e.Time).ToList();
    }

    private async Task<List<KrakenLedgerEntry>> GetLedgerBatchAsync(int offset, CancellationToken ct, double startTime = 0)
    {
        var path = "/0/private/Ledgers";
        var nonce = GetNonce().ToString();

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

        var result = JsonSerializer.Deserialize<KrakenLedgerResponse>(json);
        if (result == null)
            throw new Exception("Failed to parse Kraken API response");

        if (result.Error != null && result.Error.Count > 0)
            throw new Exception($"Kraken API error: {string.Join(", ", result.Error)}");

        if (result.Result?.Ledger == null)
            return new List<KrakenLedgerEntry>();

        var entries = new List<KrakenLedgerEntry>();
        foreach (var (ledgerId, entry) in result.Result.Ledger)
        {
            entry.LedgerId = ledgerId;
            entries.Add(entry);
        }

        return entries;
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

    // ========== PUBLIC API: OHLC for FX rates ==========

    /// <summary>
    /// Gets all currently tradeable asset pairs from Kraken's public API.
    /// Returns a dictionary mapping pair names to their details.
    /// No API key needed. Used for dynamic pair discovery.
    /// </summary>
    public async Task<Dictionary<string, KrakenAssetPairInfo>> GetAssetPairsAsync(CancellationToken ct = default)
    {
        var url = "/0/public/AssetPairs";
        var response = await _httpClient.GetAsync(url, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errors) && errors.GetArrayLength() > 0)
        {
            var errMsg = string.Join(", ", errors.EnumerateArray().Select(e => e.GetString()));
            throw new Exception($"Kraken AssetPairs error: {errMsg}");
        }

        var pairs = new Dictionary<string, KrakenAssetPairInfo>();

        if (root.TryGetProperty("result", out var result))
        {
            foreach (var prop in result.EnumerateObject())
            {
                var pairName = prop.Name;
                var details = prop.Value;

                // Extract the base and quote asset names from the pair info
                var baseAsset = details.TryGetProperty("base", out var baseProp) ? baseProp.GetString() : "";
                var quoteAsset = details.TryGetProperty("quote", out var quoteProp) ? quoteProp.GetString() : "";
                var altName = details.TryGetProperty("altname", out var altProp) ? altProp.GetString() : "";
                var wsName = details.TryGetProperty("wsname", out var wsProp) ? wsProp.GetString() : "";

                // Check if the pair is active for trading
                var status = details.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "";

                if (!string.IsNullOrEmpty(baseAsset) && !string.IsNullOrEmpty(quoteAsset))
                {
                    pairs[pairName] = new KrakenAssetPairInfo
                    {
                        PairName = pairName,
                        BaseAsset = baseAsset,
                        QuoteAsset = quoteAsset,
                        AltName = altName ?? "",
                        WsName = wsName ?? "",
                        Status = status ?? "",
                        IsActive = status == "online"
                    };
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// Downloads daily OHLC data from Kraken's public API for a given pair.
    /// Returns list of (timestamp, open, high, low, close, volume) tuples.
    /// No API key needed. Rate limited to ~1 req/sec for public endpoints.
    /// Uses daily candles (1440 minute intervals) for HMRC-compliant daily rates.
    /// </summary>
    public async Task<List<OhlcCandle>> GetOhlcDataAsync(string pair, long sinceUnixTime = 0, CancellationToken ct = default, int interval = 1440)
    {
        var url = $"/0/public/OHLC?pair={pair}&interval={interval}"; // 1440 = daily (24-hour) intervals
        if (sinceUnixTime > 0)
            url += $"&since={sinceUnixTime}";

        var response = await _httpClient.GetAsync(url, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errors) && errors.GetArrayLength() > 0)
        {
            var errMsg = string.Join(", ", errors.EnumerateArray().Select(e => e.GetString()));
            throw new Exception($"Kraken OHLC error for {pair}: {errMsg}");
        }

        var candles = new List<OhlcCandle>();

        if (root.TryGetProperty("result", out var result))
        {
            foreach (var prop in result.EnumerateObject())
            {
                if (prop.Name == "last") continue; // skip the "last" timestamp field

                foreach (var candle in prop.Value.EnumerateArray())
                {
                    var arr = candle.EnumerateArray().ToArray();
                    if (arr.Length >= 5)
                    {
                        candles.Add(new OhlcCandle
                        {
                            Timestamp = arr[0].GetInt64(),
                            Open = decimal.Parse(arr[1].GetString() ?? "0", CultureInfo.InvariantCulture),
                            High = decimal.Parse(arr[2].GetString() ?? "0", CultureInfo.InvariantCulture),
                            Low = decimal.Parse(arr[3].GetString() ?? "0", CultureInfo.InvariantCulture),
                            Close = decimal.Parse(arr[4].GetString() ?? "0", CultureInfo.InvariantCulture),
                        });
                    }
                }
            }
        }

        return candles.OrderBy(c => c.Timestamp).ToList();
    }
}

public class OhlcCandle
{
    public long Timestamp { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }

    public DateTimeOffset DateTime => DateTimeOffset.FromUnixTimeSeconds(Timestamp);

    /// <summary>
    /// Gets the rate value based on the specified FX rate type for HMRC compliance
    /// </summary>
    public decimal GetRate(FxRateType rateType) => rateType switch
    {
        FxRateType.Open => Open,
        FxRateType.High => High,
        FxRateType.Low => Low,
        FxRateType.Close => Close,
        FxRateType.Average => (High + Low) / 2m,
        _ => Close
    };
}

// ========== Response types ==========

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

public class KrakenLedgerResponse
{
    [JsonPropertyName("error")]
    public List<string>? Error { get; set; }

    [JsonPropertyName("result")]
    public KrakenLedgerResult? Result { get; set; }
}

public class KrakenLedgerResult
{
    [JsonPropertyName("ledger")]
    public Dictionary<string, KrakenLedgerEntry>? Ledger { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public class KrakenAssetPairInfo
{
    public string PairName { get; set; } = "";
    public string BaseAsset { get; set; } = "";
    public string QuoteAsset { get; set; } = "";
    public string AltName { get; set; } = "";
    public string WsName { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsActive { get; set; }
}
