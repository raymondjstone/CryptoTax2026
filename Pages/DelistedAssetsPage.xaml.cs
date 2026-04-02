using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using CryptoTax2026.Models;
using CryptoTax2026.Services;

namespace CryptoTax2026.Pages;

public sealed partial class DelistedAssetsPage : Page
{
    private MainWindow? _mainWindow;
    private KrakenPairEventsService? _pairEventsSvc;
    private bool _initializingToggle;

    // All default events loaded from the JSON, used for searching
    private List<DefaultPairViewModel> _allDefaults = new();

    public DelistedAssetsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainWindow mw)
        {
            _mainWindow = mw;
            _pairEventsSvc = KrakenPairEventsService.TryLoad();
            if (_pairEventsSvc != null)
            {
                _allDefaults = _pairEventsSvc.GetDefaultDelistEvents()
                        .Select((d, i) => new DefaultPairViewModel
                        {
                            Pair = d.Pair,
                            DelistDateFormatted = "~" + d.DelistingDate.ToString("dd/MM/yyyy"),
                            RelistDateFormatted = d.RelistDate.HasValue
                                ? "~" + d.RelistDate.Value.ToString("dd/MM/yyyy")
                                : "—",
                            DisplayText = d.RelistDate.HasValue ? "Relisted" : "Still delisted",
                            Source = d
                        })
                        .OrderBy(d => d.Pair)
                        .ToList();
            }

            // Set toggle state without firing the Toggled handler
            _initializingToggle = true;
            IgnoreAutoToggle.IsOn = mw.Settings.IgnoreAutoDelistings;
            _initializingToggle = false;
            ApplyAutoDelistingsVisibility(mw.Settings.IgnoreAutoDelistings);

            RefreshDelistedAssetsList();
            UpdateDefaultsList(filter: "");
        }
    }

    private void RefreshDelistedAssetsList()
    {
        if (_mainWindow == null) return;

        var ignore = _mainWindow.Settings.IgnoreAutoDelistings;
        var items = _mainWindow.Settings.DelistedAssets
            .Select((d, i) => (d, i))
            .Where(x => !ignore || !string.Equals(x.d.Notes, "Kraken", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                var est = x.d.Notes == "Kraken";
                return new DelistedAssetViewModel
                {
                    Pair = x.d.Pair,
                    IsEstimated = est,
                    DelistDateFormatted = (est ? "~" : "") + x.d.DelistingDate.ToString("dd/MM/yyyy"),
                    RelistDateFormatted = x.d.RelistDate.HasValue
                        ? (est ? "~" : "") + x.d.RelistDate.Value.ToString("dd/MM/yyyy")
                        : "—",
                    Notes = x.d.Notes,
                    ClaimType = x.d.ClaimType,
                    Index = x.i
                };
            })
            .ToList();

        DelistedAssetsList.ItemsSource = items;
        EmptyMessage.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Only show pairs delisted on or after the start of the 2023/24 UK tax year.
    private static readonly DateTimeOffset TaxYear2023Start = new(2023, 4, 6, 0, 0, 0, TimeSpan.Zero);

    // Quote-currency priority for deduplication. Checked longest-first so "USDT" is
    // evaluated before "USD", "ZGBP" before "GBP", etc. Lower number = preferred.
    private static readonly (string Quote, int Priority)[] QuotePriorities =
    [
        ("USDT", 2), ("USDC", 3), ("ZGBP", 0), ("ZUSD", 1), ("ZEUR", 4),
        ("GBP",  0), ("USD",  1), ("EUR",  4),
    ];

    private static int QuotePriorityFor(string pair)
    {
        var upper = pair.ToUpperInvariant();
        foreach (var (quote, priority) in QuotePriorities)
            if (upper.EndsWith(quote, StringComparison.Ordinal))
                return priority;
        return 99;
    }

    private void UpdateDefaultsList(string filter)
    {
        if (_pairEventsSvc == null)
        {
            DefaultPairsList.ItemsSource = null;
            DefaultsEmptyMessage.Visibility = Visibility.Visible;
            DefaultsEmptyMessage.Text = "Kraken pair-events database not found.";
            return;
        }

        var trimmed = filter.Trim();
        IEnumerable<DefaultPairViewModel> results = _allDefaults
            .Where(d => d.Source.DelistingDate >= TaxYear2023Start);

        if (trimmed.Length > 0)
        {
            // Free-text search: show every matching variant so the user can pick the exact pair.
            results = results.Where(d => d.Pair.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Default view: keep only the best-quote pair per underlying asset
            // (e.g. SOLGBP wins over SOLAUD; SOLUSD wins over SOLAED).
            results = results
                .GroupBy(d => d.Source.EffectiveAsset)
                .Select(g => g.OrderBy(d => QuotePriorityFor(d.Pair)).First())
                .OrderBy(d => d.Pair);
        }

        // Rebuild with fresh indices so Import button tags are correct
        var list = results
            .Select((d, i) => new DefaultPairViewModel
            {
                Pair = d.Pair,
                DelistDateFormatted = d.DelistDateFormatted,
                RelistDateFormatted = d.RelistDateFormatted,
                DisplayText = d.DisplayText,
                Source = d.Source,
                Index = i
            })
            .ToList();

        DefaultPairsList.ItemsSource = list;
        DefaultsEmptyMessage.Visibility = list.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        DefaultsEmptyMessage.Text = trimmed.Length > 0
            ? "No matching pairs found."
            : "No delisted pairs found for 2023 onwards.";
    }

    private void ApplyAutoDelistingsVisibility(bool ignore)
    {
        AutoDelistingsSection.Visibility = ignore ? Visibility.Collapsed : Visibility.Visible;

        if (ignore && _mainWindow != null)
        {
            var hiddenCount = _mainWindow.Settings.DelistedAssets
                .Count(e => string.Equals(e.Notes, "Kraken", StringComparison.OrdinalIgnoreCase));
            if (hiddenCount > 0)
            {
                var noun = hiddenCount == 1 ? "entry is" : "entries are";
                KrakenEntriesHiddenInfo.Text =
                    $"{hiddenCount} Kraken database {noun} hidden and excluded from calculations. Toggle off to show {(hiddenCount == 1 ? "it" : "them")}.";
                KrakenEntriesHiddenInfo.Visibility = Visibility.Visible;
                return;
            }
        }
        KrakenEntriesHiddenInfo.Visibility = Visibility.Collapsed;
    }

    private async void IgnoreAutoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializingToggle || _mainWindow == null) return;

        _mainWindow.Settings.IgnoreAutoDelistings = IgnoreAutoToggle.IsOn;
        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);

        ApplyAutoDelistingsVisibility(IgnoreAutoToggle.IsOn);
        RefreshDelistedAssetsList();

        if (_mainWindow.FxService != null && _mainWindow.Ledger.Count > 0)
            await _mainWindow.RecalculateAndBuildTabsAsync();
    }

    private void PairSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDefaultsList(PairSearchBox.Text ?? "");
    }

    private async void AddDelistedAsset_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var pair = DelistPairBox.Text?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(pair))
        {
            ShowInfo("Please enter a trading pair name.", InfoBarSeverity.Warning);
            return;
        }

        var selectedDelistDate = DelistDatePicker.Date;
        if (selectedDelistDate == null)
        {
            ShowInfo("Please select a delist date.", InfoBarSeverity.Warning);
            return;
        }

        var delistingDate = new DateTimeOffset(selectedDelistDate.Value.DateTime, TimeSpan.Zero);

        DateTimeOffset? relistDate = null;
        if (RelistDatePicker.Date.HasValue)
        {
            relistDate = new DateTimeOffset(RelistDatePicker.Date.Value.DateTime, TimeSpan.Zero);
            if (relistDate <= delistingDate)
            {
                ShowInfo("Relist date must be after the delist date.", InfoBarSeverity.Warning);
                return;
            }
        }

        var notes = DelistNotesBox.Text?.Trim() ?? "";
        var claimType = (ClaimTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Delisted";

        await AddPairEventAsync(pair, delistingDate, relistDate, notes, claimType);

        // Clear inputs
        DelistPairBox.Text = "";
        DelistNotesBox.Text = "";
        DelistDatePicker.Date = null;
        RelistDatePicker.Date = null;
    }

    private async void ImportDefaultPair_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        if (sender is not Button btn) return;

        // Retrieve the view-model from the filtered list via the tag index
        if (DefaultPairsList.ItemsSource is not List<DefaultPairViewModel> items) return;
        if (btn.Tag is not int index || index < 0 || index >= items.Count) return;

        var vm = items[index];
        await AddPairEventAsync(
            vm.Source.Pair,
            vm.Source.DelistingDate,
            vm.Source.RelistDate,
            vm.Source.Notes,
            vm.Source.ClaimType);
    }

    private async System.Threading.Tasks.Task AddPairEventAsync(
        string pair, DateTimeOffset delistDate, DateTimeOffset? relistDate,
        string notes, string claimType)
    {
        if (_mainWindow == null) return;

        // Allow multiple periods for the same pair (delist + relist cycles)
        _mainWindow.Settings.DelistedAssets.Add(new DelistedAssetEvent
        {
            Pair = pair,
            DelistingDate = delistDate,
            RelistDate = relistDate,
            Notes = notes,
            ClaimType = claimType
        });

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        RefreshDelistedAssetsList();

        var relistInfo = relistDate.HasValue ? $" (relisted {relistDate.Value:dd/MM/yyyy})" : "";
        ShowInfo($"Added event for {pair}: delisted {delistDate:dd/MM/yyyy}{relistInfo}.", InfoBarSeverity.Success);

        if (_mainWindow.FxService != null && _mainWindow.Ledger.Count > 0)
            await _mainWindow.RecalculateAndBuildTabsAsync();
    }

    private async void EditDelistedAsset_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        if (sender is not Button btn || btn.Tag is not int index) return;
        if (index < 0 || index >= _mainWindow.Settings.DelistedAssets.Count) return;

        var existing = _mainWindow.Settings.DelistedAssets[index];

        var delistPicker = new CalendarDatePicker
        {
            Header = "Delist Date",
            PlaceholderText = "Pick a date",
            Width = 200,
            Date = existing.DelistingDate.DateTime
        };

        var relistPicker = new CalendarDatePicker
        {
            Header = "Relist Date (optional)",
            PlaceholderText = "Pick a date",
            Width = 200,
            Date = existing.RelistDate?.DateTime
        };

        var notesBox = new TextBox
        {
            Header = "Notes",
            Text = existing.Notes ?? "",
            Width = 300
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Editing dates for {existing.Pair}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(delistPicker);
        panel.Children.Add(relistPicker);
        panel.Children.Add(notesBox);

        var dialog = new ContentDialog
        {
            Title = "Edit Delist Event",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (delistPicker.Date == null)
        {
            ShowInfo("Please select a delist date.", InfoBarSeverity.Warning);
            return;
        }

        var newDelistDate = new DateTimeOffset(delistPicker.Date.Value.DateTime, TimeSpan.Zero);

        DateTimeOffset? newRelistDate = null;
        if (relistPicker.Date.HasValue)
        {
            newRelistDate = new DateTimeOffset(relistPicker.Date.Value.DateTime, TimeSpan.Zero);
            if (newRelistDate <= newDelistDate)
            {
                ShowInfo("Relist date must be after the delist date.", InfoBarSeverity.Warning);
                return;
            }
        }

        existing.DelistingDate = newDelistDate;
        existing.RelistDate = newRelistDate;
        existing.Notes = notesBox.Text.Trim();

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        RefreshDelistedAssetsList();

        var relistInfo = newRelistDate.HasValue ? $" (relisted {newRelistDate.Value:dd/MM/yyyy})" : "";
        ShowInfo($"Updated {existing.Pair}: delisted {newDelistDate:dd/MM/yyyy}{relistInfo}.", InfoBarSeverity.Success);

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

        ShowInfo($"Removed event for {removed.Pair}.", InfoBarSeverity.Success);

        if (_mainWindow.FxService != null && _mainWindow.Ledger.Count > 0)
            await _mainWindow.RecalculateAndBuildTabsAsync();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        InfoMessage.Message = message;
        InfoMessage.Severity = severity;
        InfoMessage.IsOpen = true;
    }
}

public class DelistedAssetViewModel
{
    public string Pair { get; set; } = "";
    public bool IsEstimated { get; set; }
    public string DelistDateFormatted { get; set; } = "";
    public string RelistDateFormatted { get; set; } = "—";
    public string Notes { get; set; } = "";
    public string ClaimType { get; set; } = "Delisted";
    public int Index { get; set; }
    public string DisplayText => string.IsNullOrEmpty(Notes)
        ? $"[{ClaimType}]"
        : $"[{ClaimType}] {Notes}";
}

public class DefaultPairViewModel
{
    public string Pair { get; set; } = "";
    public string DelistDateFormatted { get; set; } = "";
    public string RelistDateFormatted { get; set; } = "—";
    public string DisplayText { get; set; } = "";
    public int Index { get; set; }
    public DelistedAssetEvent Source { get; set; } = new();
}
