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
    private static readonly string LedgerFile = Path.Combine(AppDataFolder, "ledger.json");
    private static readonly string SettingsFile = Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public TradeStorageService()
    {
        Directory.CreateDirectory(AppDataFolder);
    }

    // ========== LEDGER ==========

    public async Task SaveLedgerAsync(List<KrakenLedgerEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(LedgerFile, json);
    }

    public async Task<List<KrakenLedgerEntry>> LoadLedgerAsync()
    {
        if (!File.Exists(LedgerFile))
            return new List<KrakenLedgerEntry>();

        var json = await File.ReadAllTextAsync(LedgerFile);
        var entries = JsonSerializer.Deserialize<List<KrakenLedgerEntry>>(json) ?? new List<KrakenLedgerEntry>();

        // Always re-normalise asset names on load — handles stale data from before
        // new suffix rules (.F, .B, .M, .P) were added
        foreach (var entry in entries)
            entry.NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName(entry.Asset);

        return entries;
    }

    public async Task<List<KrakenLedgerEntry>> MergeAndSaveLedgerAsync(List<KrakenLedgerEntry> newEntries)
    {
        var existing = await LoadLedgerAsync();
        var existingIds = new HashSet<string>(existing.Select(e => e.LedgerId));

        var toAdd = newEntries.Where(e => !existingIds.Contains(e.LedgerId)).ToList();
        existing.AddRange(toAdd);

        var merged = existing.OrderBy(e => e.Time).ToList();
        await SaveLedgerAsync(merged);
        return merged;
    }

    public async Task<double> GetLatestLedgerTimeAsync()
    {
        var entries = await LoadLedgerAsync();
        if (entries.Count == 0) return 0;
        return entries.Max(e => e.Time);
    }

    public async Task DeleteLedgerAsync()
    {
        if (File.Exists(LedgerFile))
            File.Delete(LedgerFile);
        await Task.CompletedTask;
    }

    public bool HasSavedLedger() => File.Exists(LedgerFile);

    public DateTime? GetLedgerFileDate()
    {
        if (!File.Exists(LedgerFile)) return null;
        return File.GetLastWriteTime(LedgerFile);
    }

    // ========== TRADES (legacy, kept for backward compat) ==========

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

    // ========== SETTINGS ==========

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
