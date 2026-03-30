using System;
using System.Collections.Generic;
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

        _allEntries = _mainWindow.Ledger
            .OrderByDescending(entry => entry.Time)
            .Select(entry => new LedgerEntryViewModel(entry))
            .ToList();

        // Populate asset filter
        var assets = _mainWindow.Ledger
            .Select(entry => entry.NormalisedAsset)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a)
            .ToList();
        AssetFilter.ItemsSource = assets;

        // Populate type filter
        var types = _mainWindow.Ledger
            .Select(entry => entry.Type)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();
        TypeFilter.ItemsSource = types;

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
        RefIdFilter.Text = "";
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (_allEntries == null) return;

        var selectedAsset = AssetFilter.SelectedItem as string;
        var selectedType = TypeFilter.SelectedItem as string;
        var refIdSearch = RefIdFilter.Text?.Trim();

        IEnumerable<LedgerEntryViewModel> filtered = _allEntries;

        if (!string.IsNullOrEmpty(selectedAsset))
            filtered = filtered.Where(e => e.Asset.Equals(selectedAsset, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(selectedType))
            filtered = filtered.Where(e => e.Type.Equals(selectedType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(refIdSearch))
            filtered = filtered.Where(e => e.RefId.Contains(refIdSearch, StringComparison.OrdinalIgnoreCase));

        var result = filtered.ToList();
        LedgerList.ItemsSource = result;

        FilterStatusText.Text = result.Count == _allEntries.Count
            ? $"Showing all {_allEntries.Count:#,##0} entries"
            : $"Showing {result.Count:#,##0} of {_allEntries.Count:#,##0} entries";
    }
}

public class LedgerEntryViewModel
{
    private readonly KrakenLedgerEntry _entry;

    public LedgerEntryViewModel(KrakenLedgerEntry entry) => _entry = entry;

    public string DateFormatted => _entry.DateTime.ToString("dd/MM/yyyy HH:mm");
    public string Asset => _entry.NormalisedAsset;
    public string Type => _entry.Type;
    public string SubType => _entry.SubType;
    public string RefId => _entry.RefId;

    public string AmountFormatted => _entry.Amount.ToString("+0.########;-0.########;0");
    public string FeeFormatted => _entry.Fee == 0 ? "" : _entry.Fee.ToString("0.########");
    public string BalanceFormatted => _entry.Balance == 0 ? "" : _entry.Balance.ToString("0.########");

    public SolidColorBrush AmountColor => _entry.Amount >= 0
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);
}
