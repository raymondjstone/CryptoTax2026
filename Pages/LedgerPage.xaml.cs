using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using CryptoTax2026.Models;

namespace CryptoTax2026.Pages;

public sealed partial class LedgerPage : Page
{
    private MainWindow? _mainWindow;
    private List<LedgerEntryViewModel>? _allEntries;

    public LedgerPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainWindow mw)
        {
            _mainWindow = mw;
            LoadLedger();
        }
    }

    private void LoadLedger()
    {
        if (_mainWindow == null) return;

        // Kraken entries tagged as "Kraken"
        var krakenVMs = _mainWindow.Ledger
            .Select(entry => new LedgerEntryViewModel(entry, "Kraken", _mainWindow.FxService));

        // Manual/CSV entries tagged by their source
        var manualVMs = _mainWindow.Settings.ManualLedgerEntries
            .Select(m => new LedgerEntryViewModel(
                new KrakenLedgerEntry
                {
                    RefId = m.RefId,
                    Time = (double)m.Date.ToUnixTimeSeconds(),
                    Type = m.Type,
                    SubType = "",
                    Asset = m.Asset,
                    AmountStr = m.Amount.ToString(),
                    FeeStr = m.Fee.ToString(),
                    BalanceStr = "0",
                    LedgerId = m.RefId,
                    NormalisedAsset = m.NormalisedAsset
                },
                m.Source,
                _mainWindow.FxService));

        _allEntries = krakenVMs.Concat(manualVMs)
            .OrderByDescending(e => e.SortTime)
            .ToList();

        // Populate asset filter
        var assets = _allEntries
            .Select(e => e.Asset)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a)
            .ToList();
        AssetFilter.ItemsSource = assets;

        // Populate type filter
        var types = _allEntries
            .Select(e => e.Type)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();
        TypeFilter.ItemsSource = types;

        // Populate source filter
        var sources = _allEntries
            .Select(e => e.Source)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .ToList();
        SourceFilter.ItemsSource = sources;

        ApplyFilters();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void RefIdFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        AssetFilter.SelectedIndex = -1;
        TypeFilter.SelectedIndex = -1;
        SourceFilter.SelectedIndex = -1;
        RefIdFilter.Text = "";
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (_allEntries == null) return;

        var selectedAsset = AssetFilter.SelectedItem as string;
        var selectedType = TypeFilter.SelectedItem as string;
        var selectedSource = SourceFilter.SelectedItem as string;
        var refIdSearch = RefIdFilter.Text?.Trim();

        IEnumerable<LedgerEntryViewModel> filtered = _allEntries;

        if (!string.IsNullOrEmpty(selectedAsset))
            filtered = filtered.Where(e => e.Asset.Equals(selectedAsset, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(selectedType))
            filtered = filtered.Where(e => e.Type.Equals(selectedType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(selectedSource))
            filtered = filtered.Where(e => e.Source.Equals(selectedSource, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(refIdSearch))
            filtered = filtered.Where(e => e.RefId.Contains(refIdSearch, StringComparison.OrdinalIgnoreCase));

        var result = filtered.ToList();
        LedgerList.ItemsSource = result;

        FilterStatusText.Text = result.Count == _allEntries.Count
            ? $"Showing all {_allEntries.Count:#,##0} entries"
            : $"Showing {result.Count:#,##0} of {_allEntries.Count:#,##0} entries";
    }

    private async void LedgerList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_mainWindow == null || e.ClickedItem is not LedgerEntryViewModel vm) return;

        var entry = vm.UnderlyingEntry;

        var splitAmountBox = new TextBox
        {
            Header = "Amount for first part",
            PlaceholderText = "e.g. half of the original",
            Text = (entry.Amount / 2).ToString("0.########")
        };
        var splitFeeBox = new TextBox
        {
            Header = "Fee for first part",
            PlaceholderText = "0",
            Text = (entry.Fee / 2).ToString("0.########")
        };

        var info = new TextBlock
        {
            Text = $"Split: {entry.NormalisedAsset} | Amount: {entry.Amount:0.########} | Fee: {entry.Fee:0.########}\n" +
                   $"Date: {entry.DateTime:dd/MM/yyyy HH:mm} | Type: {entry.Type} | Ref: {entry.RefId}\n\n" +
                   "The remainder will be created as a second entry.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(info);
        panel.Children.Add(splitAmountBox);
        panel.Children.Add(splitFeeBox);

        var dialog = new ContentDialog
        {
            Title = "Split Transaction",
            Content = panel,
            PrimaryButtonText = "Split",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (!decimal.TryParse(splitAmountBox.Text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var splitAmount))
            return;
        decimal splitFee = 0;
        decimal.TryParse(splitFeeBox.Text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out splitFee);

        var remainAmount = entry.Amount - splitAmount;
        var remainFee = entry.Fee - splitFee;
        var normalised = KrakenLedgerEntry.NormaliseAssetName(entry.Asset);

        _mainWindow.Settings.ManualLedgerEntries.Add(new ManualLedgerEntry
        {
            Source = "Split",
            RefId = $"SPLIT-{entry.RefId}-A",
            Date = entry.DateTime,
            Type = entry.Type,
            Asset = entry.Asset,
            Amount = splitAmount,
            Fee = Math.Abs(splitFee),
            NormalisedAsset = normalised
        });
        _mainWindow.Settings.ManualLedgerEntries.Add(new ManualLedgerEntry
        {
            Source = "Split",
            RefId = $"SPLIT-{entry.RefId}-B",
            Date = entry.DateTime,
            Type = entry.Type,
            Asset = entry.Asset,
            Amount = remainAmount,
            Fee = Math.Abs(remainFee),
            NormalisedAsset = normalised
        });

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        LoadLedger();

        if (_mainWindow.FxService != null && _mainWindow.Ledger.Count > 0)
            await _mainWindow.RecalculateAndBuildTabsAsync();
    }
}

public class LedgerEntryViewModel
{
    private readonly KrakenLedgerEntry _entry;
    private readonly string _gbpRateFormatted;
    private readonly string _rateDateFormatted;

    public LedgerEntryViewModel(KrakenLedgerEntry entry, string source = "Kraken", Services.FxConversionService? fxService = null)
    {
        _entry = entry;
        Source = source;

        // Calculate FX rate information if available
        if (fxService != null && !string.IsNullOrEmpty(entry.NormalisedAsset) && entry.NormalisedAsset != "GBP")
        {
            try
            {
                // Get the actual rate used for this transaction
                var gbpValue = fxService.ConvertToGbp(1m, entry.NormalisedAsset, entry.DateTime);
                if (gbpValue > 0)
                {
                    _gbpRateFormatted = gbpValue.ToString("F4");

                    // Get rate information to determine the source and timing
                    var rateInfo = fxService.GetRateInfo(entry.NormalisedAsset, entry.DateTime);
                    _rateDateFormatted = rateInfo ?? entry.DateTime.ToString("dd/MM HH:mm");
                }
                else
                {
                    _gbpRateFormatted = "N/A";
                    _rateDateFormatted = "N/A";
                }
            }
            catch
            {
                _gbpRateFormatted = "Error";
                _rateDateFormatted = "Error";
            }
        }
        else
        {
            _gbpRateFormatted = entry.NormalisedAsset == "GBP" ? "1.0000" : "N/A";
            _rateDateFormatted = entry.NormalisedAsset == "GBP" ? "GBP" : "N/A";
        }
    }

    public KrakenLedgerEntry UnderlyingEntry => _entry;
    public double SortTime => _entry.Time;
    public string DateFormatted => _entry.DateTime.ToString("dd/MM/yyyy HH:mm");
    public string Source { get; }
    public string Asset => _entry.NormalisedAsset;
    public string Type => _entry.Type;
    public string SubType => _entry.SubType;
    public string RefId => _entry.RefId;

    public string AmountFormatted => _entry.Amount.ToString("+0.########;-0.########;0");
    public string FeeFormatted => _entry.Fee == 0 ? "" : _entry.Fee.ToString("0.########");
    public string BalanceFormatted => _entry.Balance == 0 ? "" : _entry.Balance.ToString("0.########");
    public string GbpRateFormatted => _gbpRateFormatted;
    public string RateDateFormatted => _rateDateFormatted;

    public SolidColorBrush AmountColor => _entry.Amount >= 0
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);
}
