using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using CryptoTax2026.Models;

namespace CryptoTax2026.Pages;

public sealed partial class DelistedAssetsPage : Page
{
    private MainWindow? _mainWindow;

    public DelistedAssetsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainWindow mw)
        {
            _mainWindow = mw;
            RefreshDelistedAssetsList();
        }
    }

    private void RefreshDelistedAssetsList()
    {
        if (_mainWindow == null) return;



        var items = _mainWindow.Settings.DelistedAssets
            .Select((d, i) => new DelistedAssetViewModel
            {
                Asset = d.Asset,
                DateFormatted = d.DelistingDate.ToString("dd/MM/yyyy"),
                Notes = d.Notes,
                ClaimType = d.ClaimType,
                Index = i
            })
            .ToList();

        DelistedAssetsList.ItemsSource = items;
        EmptyMessage.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddDelistedAsset_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var asset = DelistAssetBox.Text?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(asset))
        {
            InfoMessage.Message = "Please enter an asset name.";
            InfoMessage.Severity = InfoBarSeverity.Warning;
            InfoMessage.IsOpen = true;
            return;
        }

        var selectedDate = DelistDatePicker.Date;
        if (selectedDate == null)
        {
            InfoMessage.Message = "Please select a delisting date.";
            InfoMessage.Severity = InfoBarSeverity.Warning;
            InfoMessage.IsOpen = true;
            return;
        }

        var delistingDate = new DateTimeOffset(selectedDate.Value.DateTime, TimeSpan.Zero);
        var notes = DelistNotesBox.Text?.Trim() ?? "";

        // Normalise the asset name using the same logic as ledger entries
        var normalised = KrakenLedgerEntry.NormaliseAssetName(asset);

        // Check for duplicates
        if (_mainWindow.Settings.DelistedAssets.Any(d =>
            string.Equals(d.Asset, normalised, StringComparison.OrdinalIgnoreCase)))
        {
            InfoMessage.Message = $"Asset '{normalised}' is already in the delisted list.";
            InfoMessage.Severity = InfoBarSeverity.Warning;
            InfoMessage.IsOpen = true;
            return;
        }

        var claimType = (ClaimTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Delisted";
        _mainWindow.Settings.DelistedAssets.Add(new DelistedAssetEvent
        {
            Asset = normalised,
            DelistingDate = delistingDate,
            Notes = notes,
            ClaimType = claimType
        });

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        RefreshDelistedAssetsList();

        // Clear inputs
        DelistAssetBox.Text = "";
        DelistNotesBox.Text = "";

        InfoMessage.Message = $"Added delisting event for {normalised} on {delistingDate:dd/MM/yyyy}.";
        InfoMessage.Severity = InfoBarSeverity.Success;
        InfoMessage.IsOpen = true;

        // Recalculate if we have data
        if (_mainWindow.FxService != null && _mainWindow.Ledger.Count > 0)
            await _mainWindow.RecalculateAndBuildTabsAsync();
    }

    private async void RemoveDelistedAsset_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        if (sender is not Button btn || btn.Tag is not int index) return;
        if (index < 0 || index >= _mainWindow.Settings.DelistedAssets.Count) return;

        var removed = _mainWindow.Settings.DelistedAssets[index];
        _mainWindow.Settings.DelistedAssets.RemoveAt(index);

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        RefreshDelistedAssetsList();

        InfoMessage.Message = $"Removed delisting event for {removed.Asset}.";
        InfoMessage.Severity = InfoBarSeverity.Success;
        InfoMessage.IsOpen = true;

        if (_mainWindow.FxService != null && _mainWindow.Ledger.Count > 0)
            await _mainWindow.RecalculateAndBuildTabsAsync();
    }
}

public class DelistedAssetViewModel
{
    public string Asset { get; set; } = "";
    public string DateFormatted { get; set; } = "";
    public string Notes { get; set; } = "";
    public string ClaimType { get; set; } = "Delisted";
    public int Index { get; set; }
    public string DisplayText => string.IsNullOrEmpty(Notes)
        ? $"[{ClaimType}]"
        : $"[{ClaimType}] {Notes}";
}
