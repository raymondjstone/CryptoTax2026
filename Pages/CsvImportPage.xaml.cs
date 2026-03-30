using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using CryptoTax2026.Models;

namespace CryptoTax2026.Pages;

public sealed partial class CsvImportPage : Page
{
    private MainWindow? _mainWindow;

    public CsvImportPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainWindow mw)
        {
            _mainWindow = mw;
            RefreshProfiles();
            RefreshImportedEntries();
        }
    }

    private void RefreshProfiles()
    {
        if (_mainWindow == null) return;
        var names = _mainWindow.Settings.CsvMappings.Select(m => m.ProfileName).ToList();
        ProfileBox.ItemsSource = names;
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_mainWindow == null) return;
        var name = ProfileBox.SelectedItem as string;
        if (string.IsNullOrEmpty(name)) return;

        var mapping = _mainWindow.Settings.CsvMappings.FirstOrDefault(m => m.ProfileName == name);
        if (mapping == null) return;

        ProfileNameBox.Text = mapping.ProfileName;
        MapDate.Text = mapping.DateColumn;
        MapDateFormat.Text = mapping.DateFormat;
        MapType.Text = mapping.TypeColumn;
        MapAsset.Text = mapping.AssetColumn;
        MapAmount.Text = mapping.AmountColumn;
        MapFee.Text = mapping.FeeColumn;
        MapFeeAsset.Text = mapping.FeeAssetColumn;
        MapPrice.Text = mapping.PriceColumn;
        MapQuoteCurrency.Text = mapping.QuoteCurrencyColumn;
        HasHeaderCheck.IsChecked = mapping.HasHeader;
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        var name = ProfileNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowInfo("Enter a profile name.", InfoBarSeverity.Warning);
            return;
        }

        var existing = _mainWindow.Settings.CsvMappings.FirstOrDefault(m =>
            string.Equals(m.ProfileName, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            _mainWindow.Settings.CsvMappings.Remove(existing);

        _mainWindow.Settings.CsvMappings.Add(new CsvImportMapping
        {
            ProfileName = name,
            DateColumn = MapDate.Text?.Trim() ?? "",
            DateFormat = MapDateFormat.Text?.Trim() ?? "yyyy-MM-dd HH:mm:ss",
            TypeColumn = MapType.Text?.Trim() ?? "",
            AssetColumn = MapAsset.Text?.Trim() ?? "",
            AmountColumn = MapAmount.Text?.Trim() ?? "",
            FeeColumn = MapFee.Text?.Trim() ?? "",
            FeeAssetColumn = MapFeeAsset.Text?.Trim() ?? "",
            PriceColumn = MapPrice.Text?.Trim() ?? "",
            QuoteCurrencyColumn = MapQuoteCurrency.Text?.Trim() ?? "",
            HasHeader = HasHeaderCheck.IsChecked == true
        });

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        RefreshProfiles();
        ProfileBox.SelectedItem = name;
        ShowInfo($"Profile '{name}' saved.", InfoBarSeverity.Success);
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        var name = ProfileBox.SelectedItem as string;
        if (string.IsNullOrEmpty(name)) return;

        _mainWindow.Settings.CsvMappings.RemoveAll(m => m.ProfileName == name);
        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        RefreshProfiles();
        ProfileBox.SelectedIndex = -1;
        ShowInfo($"Profile '{name}' deleted.", InfoBarSeverity.Success);
    }

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var dateCol = MapDate.Text?.Trim();
        var assetCol = MapAsset.Text?.Trim();
        var amountCol = MapAmount.Text?.Trim();
        if (string.IsNullOrEmpty(dateCol) || string.IsNullOrEmpty(assetCol) || string.IsNullOrEmpty(amountCol))
        {
            ShowInfo("Date, Asset, and Amount columns are required.", InfoBarSeverity.Warning);
            return;
        }

        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            var lines = await File.ReadAllLinesAsync(file.Path);
            if (lines.Length == 0)
            {
                ShowInfo("File is empty.", InfoBarSeverity.Warning);
                return;
            }

            var hasHeader = HasHeaderCheck.IsChecked == true;
            var headers = hasHeader ? ParseCsvLine(lines[0]) : null;
            var dataStart = hasHeader ? 1 : 0;

            int dateIdx = FindColumnIndex(headers, dateCol, dataStart);
            int assetIdx = FindColumnIndex(headers, assetCol, dataStart);
            int amountIdx = FindColumnIndex(headers, amountCol, dataStart);
            int typeIdx = FindColumnIndex(headers, MapType.Text?.Trim(), dataStart);
            int feeIdx = FindColumnIndex(headers, MapFee.Text?.Trim(), dataStart);

            if (dateIdx < 0 || assetIdx < 0 || amountIdx < 0)
            {
                ShowInfo($"Could not find required columns. Available: {string.Join(", ", headers ?? Array.Empty<string>())}", InfoBarSeverity.Error);
                return;
            }

            var profileName = ProfileNameBox.Text?.Trim() ?? "CSV Import";
            var dateFormat = MapDateFormat.Text?.Trim() ?? "yyyy-MM-dd HH:mm:ss";
            var imported = new List<ManualLedgerEntry>();
            int errors = 0;

            for (int i = dataStart; i < lines.Length; i++)
            {
                var fields = ParseCsvLine(lines[i]);
                if (fields.Length <= Math.Max(dateIdx, Math.Max(assetIdx, amountIdx))) { errors++; continue; }

                if (!DateTimeOffset.TryParseExact(fields[dateIdx].Trim(), dateFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                {
                    // Try generic parse as fallback
                    if (!DateTimeOffset.TryParse(fields[dateIdx].Trim(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out date))
                    {
                        errors++;
                        continue;
                    }
                }

                var asset = fields[assetIdx].Trim().ToUpperInvariant();
                if (!decimal.TryParse(fields[amountIdx].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                { errors++; continue; }

                var type = typeIdx >= 0 && typeIdx < fields.Length ? fields[typeIdx].Trim().ToLowerInvariant() : "trade";
                decimal fee = 0;
                if (feeIdx >= 0 && feeIdx < fields.Length)
                    decimal.TryParse(fields[feeIdx].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out fee);

                var normalised = KrakenLedgerEntry.NormaliseAssetName(asset);
                var refId = $"CSV-{profileName}-{i}";

                imported.Add(new ManualLedgerEntry
                {
                    Source = profileName,
                    RefId = refId,
                    Date = date,
                    Type = type,
                    Asset = asset,
                    Amount = amount,
                    Fee = Math.Abs(fee),
                    NormalisedAsset = normalised
                });
            }

            _mainWindow.Settings.ManualLedgerEntries.AddRange(imported);
            await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
            RefreshImportedEntries();

            var msg = $"Imported {imported.Count} entries from {Path.GetFileName(file.Path)}.";
            if (errors > 0) msg += $" {errors} rows skipped due to parse errors.";
            ShowInfo(msg, errors > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);

            // Recalculate
            if (_mainWindow.FxService != null && _mainWindow.Ledger.Count > 0)
                await _mainWindow.RecalculateAndBuildTabsAsync();
        }
        catch (Exception ex)
        {
            ShowInfo($"Import failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ClearImports_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        _mainWindow.Settings.ManualLedgerEntries.Clear();
        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        RefreshImportedEntries();
        ShowInfo("All imported entries cleared.", InfoBarSeverity.Success);

        if (_mainWindow.FxService != null && _mainWindow.Ledger.Count > 0)
            await _mainWindow.RecalculateAndBuildTabsAsync();
    }

    private void RefreshImportedEntries()
    {
        if (_mainWindow == null) return;
        var entries = _mainWindow.Settings.ManualLedgerEntries;
        ImportedCountText.Text = entries.Count > 0 ? $"{entries.Count} manually imported entries" : "";
        ClearImportsBtn.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ImportedEntriesList.ItemsSource = entries
            .OrderByDescending(e => e.Date)
            .Select(e => new ManualEntryViewModel(e))
            .ToList();
    }

    private static int FindColumnIndex(string[]? headers, string? colName, int dataStart)
    {
        if (string.IsNullOrEmpty(colName)) return -1;
        if (headers != null)
        {
            for (int i = 0; i < headers.Length; i++)
                if (string.Equals(headers[i].Trim(), colName, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        // Try as numeric index
        if (int.TryParse(colName, out var idx)) return idx;
        return -1;
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

    private void ShowInfo(string msg, InfoBarSeverity severity)
    {
        InfoMessage.Message = msg;
        InfoMessage.Severity = severity;
        InfoMessage.IsOpen = true;
    }
}

public class ManualEntryViewModel
{
    private readonly ManualLedgerEntry _entry;
    public ManualEntryViewModel(ManualLedgerEntry entry) => _entry = entry;

    public string DateFormatted => _entry.Date.ToString("dd/MM/yyyy HH:mm");
    public string Asset => _entry.NormalisedAsset;
    public string Type => _entry.Type;
    public string AmountFormatted => _entry.Amount.ToString("+0.########;-0.########;0");
    public string FeeFormatted => _entry.Fee == 0 ? "" : _entry.Fee.ToString("0.########");
    public string Source => _entry.Source;
}
