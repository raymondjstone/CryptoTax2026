using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

public class TradeStorageService
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoTax2026");

    private static readonly string TradesFile = Path.Combine(AppDataFolder, "trades.json");
    private static readonly string SettingsFile = Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public TradeStorageService()
    {
        Directory.CreateDirectory(AppDataFolder);
    }

    public async Task SaveTradesAsync(List<KrakenTrade> trades)
    {
        var json = JsonSerializer.Serialize(trades, JsonOptions);
        await File.WriteAllTextAsync(TradesFile, json);
    }

    public async Task<List<KrakenTrade>> LoadTradesAsync()
    {
        if (!File.Exists(TradesFile))
            return new List<KrakenTrade>();

        var json = await File.ReadAllTextAsync(TradesFile);
        return JsonSerializer.Deserialize<List<KrakenTrade>>(json) ?? new List<KrakenTrade>();
    }

    /// <summary>
    /// Merges new trades into existing stored trades, deduplicating by TradeId.
    /// Returns the combined list.
    /// </summary>
    public async Task<List<KrakenTrade>> MergeAndSaveTradesAsync(List<KrakenTrade> newTrades)
    {
        var existing = await LoadTradesAsync();
        var existingIds = new HashSet<string>(existing.Select(t => t.TradeId));

        var toAdd = newTrades.Where(t => !existingIds.Contains(t.TradeId)).ToList();
        existing.AddRange(toAdd);

        var merged = existing.OrderBy(t => t.Time).ToList();
        await SaveTradesAsync(merged);
        return merged;
    }

    /// <summary>
    /// Returns the unix timestamp of the latest stored trade, or 0 if none.
    /// </summary>
    public async Task<double> GetLatestTradeTimeAsync()
    {
        var trades = await LoadTradesAsync();
        if (trades.Count == 0) return 0;
        return trades.Max(t => t.Time);
    }

    public async Task DeleteTradesAsync()
    {
        if (File.Exists(TradesFile))
            File.Delete(TradesFile);
        await Task.CompletedTask;
    }

    public bool HasSavedTrades() => File.Exists(TradesFile);

    public DateTime? GetTradesFileDate()
    {
        if (!File.Exists(TradesFile)) return null;
        return File.GetLastWriteTime(TradesFile);
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(SettingsFile, json);
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        if (!File.Exists(SettingsFile))
            return new AppSettings();

        var json = await File.ReadAllTextAsync(SettingsFile);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public string GetDataFolderPath() => AppDataFolder;
}
