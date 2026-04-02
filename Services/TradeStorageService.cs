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
        if (!File.Exists(_settingsFile))
            return new AppSettings();

        var json = await File.ReadAllTextAsync(_settingsFile);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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

        Console.WriteLine($"[DEBUG] Custom data folder: {_dataFolder}");
        Console.WriteLine($"[DEBUG] Current FX cache folder: {fxCacheFolder}");
        Console.WriteLine($"[DEBUG] Default FX cache folder: {defaultFxCacheFolder}");

        // CRITICAL: List files that must NEVER be deleted
        var protectedFiles = new[] { "manual_overrides.json", "pairmap.json" };

        // Process both FX cache locations
        var foldersToClean = new List<string>();

        if (Directory.Exists(fxCacheFolder))
        {
            foldersToClean.Add(fxCacheFolder);
            Console.WriteLine($"[DEBUG] Found FX cache in custom location: {fxCacheFolder}");
        }

        if (Directory.Exists(defaultFxCacheFolder) && defaultFxCacheFolder != fxCacheFolder)
        {
            foldersToClean.Add(defaultFxCacheFolder);
            Console.WriteLine($"[DEBUG] Found FX cache in default location: {defaultFxCacheFolder}");
        }

        if (foldersToClean.Count == 0)
        {
            Console.WriteLine("[DEBUG] No FX cache folders found to clean");
            return;
        }

        // Clean each FX cache folder by selectively deleting files
        foreach (var folderPath in foldersToClean)
        {
            Console.WriteLine($"[DEBUG] Cleaning FX cache folder: {folderPath}");

            // SAFETY CHECK: Ensure we're only working with fx_cache folders
            if (!folderPath.EndsWith("fx_cache", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[ERROR] SAFETY CHECK FAILED: Not an fx_cache folder: {folderPath}");
                throw new InvalidOperationException($"Safety check failed: Cannot process folder that is not fx_cache: {folderPath}");
            }

            // Get all files in the fx_cache folder
            var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
            Console.WriteLine($"[DEBUG] Found {allFiles.Length} total files in: {folderPath}");

            int deletedCount = 0;
            int protectedCount = 0;

            foreach (var filePath in allFiles)
            {
                var fileName = Path.GetFileName(filePath);

                // NEVER delete protected files
                if (protectedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[DEBUG] PROTECTED (not deleting): {filePath}");
                    protectedCount++;
                    continue;
                }

                // Delete all other files (cached rate files)
                try
                {
                    File.Delete(filePath);
                    Console.WriteLine($"[DEBUG] Deleted cached rate file: {filePath}");
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNING] Failed to delete {filePath}: {ex.Message}");
                }
            }

            Console.WriteLine($"[DEBUG] Folder {folderPath}: Deleted {deletedCount} files, Protected {protectedCount} files");
        }

        // FINAL VERIFICATION: Ensure manual_overrides.json still exists
        var manualOverridesPath = Path.Combine(fxCacheFolder, "manual_overrides.json");
        if (File.Exists(manualOverridesPath))
        {
            Console.WriteLine($"[DEBUG] VERIFIED: manual_overrides.json is safe: {manualOverridesPath}");
        }
        else
        {
            Console.WriteLine($"[DEBUG] manual_overrides.json was not present: {manualOverridesPath}");
        }

        Console.WriteLine($"[DEBUG] FX cache reset complete - manual_overrides.json was NEVER touched");
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
