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
    private readonly string _dataFolder;
    private readonly string _ledgerFile;
    private readonly string _settingsFile;

    // Separate custom path config file - ALWAYS in default AppData location
    private static readonly string CustomPathConfigFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoTax2026", "custom_path.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public TradeStorageService(string? customDataPath = null)
    {
        _dataFolder = customDataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoTax2026");

        _ledgerFile = Path.Combine(_dataFolder, "ledger.json");
        _settingsFile = Path.Combine(_dataFolder, "settings.json");

        Directory.CreateDirectory(_dataFolder);
    }

    // ========== LEDGER ==========

    public async Task SaveLedgerAsync(List<KrakenLedgerEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(_ledgerFile, json);
    }

    public async Task<List<KrakenLedgerEntry>> LoadLedgerAsync()
    {
        if (!File.Exists(_ledgerFile))
            return new List<KrakenLedgerEntry>();

        var json = await File.ReadAllTextAsync(_ledgerFile);
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
        if (File.Exists(_ledgerFile))
            File.Delete(_ledgerFile);
        await Task.CompletedTask;
    }

    public bool HasSavedLedger() => File.Exists(_ledgerFile);

    public DateTime? GetLedgerFileDate()
    {
        if (!File.Exists(_ledgerFile)) return null;
        return File.GetLastWriteTime(_ledgerFile);
    }


    // ========== CUSTOM PATH CONFIGURATION ==========

    /// <summary>
    /// Loads the custom data path from the dedicated config file in default AppData.
    /// Returns null if no custom path is configured.
    /// </summary>
    public static string? LoadCustomDataPath()
    {
        if (!File.Exists(CustomPathConfigFile))
            return null;

        try
        {
            var json = File.ReadAllText(CustomPathConfigFile);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("customPath").GetString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the custom data path to the dedicated config file in default AppData.
    /// Pass null to remove the custom path configuration.
    /// </summary>
    public static void SaveCustomDataPath(string? customPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CustomPathConfigFile)!);

            if (string.IsNullOrEmpty(customPath))
            {
                // Remove custom path config
                if (File.Exists(CustomPathConfigFile))
                    File.Delete(CustomPathConfigFile);
            }
            else
            {
                var config = new { customPath };
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(CustomPathConfigFile, json);
            }
        }
        catch
        {
            // Silently fail - not critical
        }
    }

    // ========== SETTINGS ==========

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        // Always save the custom path to the dedicated config file in default AppData
        SaveCustomDataPath(settings.CustomDataPath);

        // Save all settings (including CustomDataPath) to the current data folder
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(_settingsFile, json);
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        AppSettings settings;

        if (!File.Exists(_settingsFile))
        {
            settings = new AppSettings();
        }
        else
        {
            var json = await File.ReadAllTextAsync(_settingsFile);
            settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            // Migrate pre-pair-based entries: if Asset is set but Pair is empty, promote Asset → Pair
            foreach (var evt in settings.DelistedAssets)
            {
                if (string.IsNullOrWhiteSpace(evt.Pair) && !string.IsNullOrWhiteSpace(evt.Asset))
                {
                    evt.Pair = evt.Asset;
                    evt.Asset = "";
                }
            }

            // Migrate manually-added entries from the pre-ClaimType-semantics era.
            // Before this version, all DelistedAssetEvents triggered £0 disposal + entry
            // suppression regardless of ClaimType. Now only "Negligible Value" does.
            // Entries imported from the Kraken JSON default list have Notes="Kraken" and
            // correctly stay as "Delisted" (informational). Manually added entries (any other
            // Notes value) were intentionally added to crystallise a capital loss and must be
            // upgraded to "Negligible Value" to preserve that behaviour.
            foreach (var evt in settings.DelistedAssets)
            {
                if (string.Equals(evt.ClaimType, "Delisted", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(evt.Notes, "Kraken", StringComparison.OrdinalIgnoreCase))
                {
                    evt.ClaimType = "Negligible Value";
                    settings.AuditLog.Add(new AuditLogEntry
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Action = "SettingsMigrated",
                        Detail = $"Auto-upgraded '{evt.Pair}' ClaimType from 'Delisted' to 'Negligible Value' " +
                                 "(manually-added entries now require 'Negligible Value' to trigger a £0 disposal)."
                    });
                }
            }
        }

        return settings;
    }

    public string GetDataFolderPath() => _dataFolder;

    public void DeleteFxCache()
    {
        var fxCacheFolder = Path.Combine(_dataFolder, "fx_cache");

        // Also check default folder in case files are there from before custom path was set
        var defaultDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoTax2026");
        var defaultFxCacheFolder = Path.Combine(defaultDataFolder, "fx_cache");

        // CRITICAL: List files that must NEVER be deleted
        var protectedFiles = new[] { "manual_overrides.json", "pairmap.json" };

        // Process both FX cache locations
        var foldersToClean = new List<string>();

        if (Directory.Exists(fxCacheFolder))
            foldersToClean.Add(fxCacheFolder);

        if (Directory.Exists(defaultFxCacheFolder) && defaultFxCacheFolder != fxCacheFolder)
            foldersToClean.Add(defaultFxCacheFolder);

        if (foldersToClean.Count == 0)
            return;

        // Clean each FX cache folder by selectively deleting files
        foreach (var folderPath in foldersToClean)
        {
            // SAFETY CHECK: Ensure we're only working with fx_cache folders
            if (!folderPath.EndsWith("fx_cache", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Safety check failed: Cannot process folder that is not fx_cache: {folderPath}");

            var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);

            foreach (var filePath in allFiles)
            {
                var fileName = Path.GetFileName(filePath);

                // NEVER delete protected files
                if (protectedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    continue;

                try { File.Delete(filePath); }
                catch { /* best-effort deletion */ }
            }
        }
    }

    public async Task MigrateDataAsync(string fromPath)
    {
        if (!Directory.Exists(fromPath) || _dataFolder == fromPath)
            return;

        // Migrate ledger.json
        var fromLedgerFile = Path.Combine(fromPath, "ledger.json");
        if (File.Exists(fromLedgerFile) && !File.Exists(_ledgerFile))
        {
            File.Copy(fromLedgerFile, _ledgerFile);
        }

        // Migrate fx_cache folder
        var fromFxCacheFolder = Path.Combine(fromPath, "fx_cache");
        var toFxCacheFolder = Path.Combine(_dataFolder, "fx_cache");
        if (Directory.Exists(fromFxCacheFolder) && !Directory.Exists(toFxCacheFolder))
        {
            Directory.CreateDirectory(toFxCacheFolder);
            foreach (var file in Directory.GetFiles(fromFxCacheFolder, "*.json"))
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(toFxCacheFolder, fileName);
                File.Copy(file, destFile);
            }
        }

        await Task.CompletedTask;
    }

    public async Task MigrateDataFromDefaultPathAsync()
    {
        var defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoTax2026");

        await MigrateDataAsync(defaultFolder);
    }

    // ========== BACKUP / RESTORE HELPERS ==========

    public List<string> GetAllDataFiles()
    {
        var files = new List<string>(10); // Preallocate capacity for estimated file count

        // Add main data files if they exist
        if (File.Exists(_ledgerFile)) files.Add(_ledgerFile);
        if (File.Exists(_settingsFile)) files.Add(_settingsFile);

        // Add FX cache files
        var fxCacheFolder = Path.Combine(_dataFolder, "fx_cache");
        if (Directory.Exists(fxCacheFolder))
        {
            files.AddRange(Directory.GetFiles(fxCacheFolder, "*.json"));
        }

        return files;
    }

    public async Task BackupAllDataAsync(string backupFolder)
    {
        Directory.CreateDirectory(backupFolder);

        var allFiles = GetAllDataFiles();

        foreach (var file in allFiles)
        {
            var relativePath = Path.GetRelativePath(_dataFolder, file);
            var backupPath = Path.Combine(backupFolder, relativePath);

            // Create directory if needed
            var backupDir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(backupDir))
                Directory.CreateDirectory(backupDir);

            File.Copy(file, backupPath, overwrite: true);
        }

        await Task.CompletedTask;
    }

    public async Task RestoreAllDataAsync(string backupFolder)
    {
        if (!Directory.Exists(backupFolder))
            throw new DirectoryNotFoundException($"Backup folder not found: {backupFolder}");

        // Clear existing data first
        await ClearAllDataAsync();

        // Restore all files from backup
        foreach (var backupFile in Directory.GetFiles(backupFolder, "*.json", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(backupFolder, backupFile);
            var targetPath = Path.Combine(_dataFolder, relativePath);

            // Create directory if needed
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(backupFile, targetPath, overwrite: true);
        }

        await Task.CompletedTask;
    }

    public async Task ClearAllDataAsync()
    {
        // Delete main data files
        if (File.Exists(_ledgerFile)) File.Delete(_ledgerFile);
        if (File.Exists(_settingsFile)) File.Delete(_settingsFile);

        // Delete FX cache
        DeleteFxCache();

        await Task.CompletedTask;
    }

    public long GetTotalDataSize()
    {
        return GetAllDataFiles().Where(File.Exists).Sum(f => new FileInfo(f).Length);
    }

    public int GetDataFileCount()
    {
        return GetAllDataFiles().Count;
    }
}
