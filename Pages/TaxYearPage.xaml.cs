using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using CryptoTax2026.Models;
using CryptoTax2026.Services;

namespace CryptoTax2026.Pages;

public sealed partial class TaxYearPage : Page
{
    private MainWindow? _mainWindow;
    private TaxYearSummary? _summary;
    private bool _isLoading;
    private List<DisposalViewModel>? _allDisposals;
    private List<BnbDisposalViewModel> _allBnbDisposals = new();

    public TaxYearPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is (MainWindow mw, TaxYearSummary summary))
        {
            _mainWindow = mw;
            _summary = summary;

            LoadingPanel.Visibility = Visibility.Visible;
            ContentScroller.Visibility = Visibility.Collapsed;
            _isLoading = true;

            // Capture all data needed by the background thread up front
            var ledger = mw.Ledger ?? new List<KrakenLedgerEntry>();
            var costOverrides = mw.Settings.CostBasisOverrides ?? new();
            var allNotes = mw.Settings.DisposalNotes ?? new();

            // Build expensive ViewModels on a background thread so the spinner actually animates
            List<WarningViewModel>? warningVms = null;
            List<StakingAssetSummaryViewModel>? stakingAssetVms = null;
            List<StakingViewModel>? stakingVms = null;
            List<AssetPnlViewModel>? assetPnlVms = null;

            await Task.Run(() =>
            {
                // Build refId lookup — no need to sort the full ledger, just group it
                var ledgerByRefId = new Dictionary<string, List<KrakenLedgerEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in ledger)
                {
                    if (string.IsNullOrEmpty(entry.RefId)) continue;
                    if (!ledgerByRefId.TryGetValue(entry.RefId, out var list))
                    {
                        list = new List<KrakenLedgerEntry>(4);
                        ledgerByRefId[entry.RefId] = list;
                    }
                    list.Add(entry);
                }

                List<BnbLedgerEntryViewModel> GetEntries(string refId)
                {
                    if (string.IsNullOrEmpty(refId))
                        return [];

                    if (!ledgerByRefId.TryGetValue(refId, out var list) || list.Count == 0)
                        return [];

                    var vms = new List<BnbLedgerEntryViewModel>(list.Count);
                    foreach (var x in list)
                        vms.Add(new BnbLedgerEntryViewModel(x, mw.FxService));
                    return vms;
                }

                var orderedDisposals = summary.Disposals
                    .OrderBy(d => d.Date)
                    .ToList();

                // Single pass: build both BnB and all-disposal lists
                _allBnbDisposals = new List<BnbDisposalViewModel>(Math.Min(orderedDisposals.Count, 128));
                _allDisposals = new List<DisposalViewModel>(orderedDisposals.Count);
                foreach (var d in orderedDisposals)
                {
                    if (d.MatchingRule.Contains("Bed", StringComparison.OrdinalIgnoreCase) ||
                        d.MatchingRule.Contains("B&B", StringComparison.OrdinalIgnoreCase) ||
                        d.MatchingRule.Contains("Breakfast", StringComparison.OrdinalIgnoreCase))
                    {
                        var de = GetEntries(d.TradeId);
                        var ae = GetEntries(d.AcquisitionRefId ?? "");
                        _allBnbDisposals.Add(new BnbDisposalViewModel(d, de, ae));
                    }

                    _allDisposals.Add(new DisposalViewModel(d,
                        costOverrides.ContainsKey(d.TradeId),
                        allNotes.ContainsKey(d.TradeId),
                        GetEntries));
                }

                // Pre-build warning VMs on background thread
                if (summary.Warnings.Count > 0)
                {
                    warningVms = summary.Warnings
                        .OrderByDescending(w => w.Level)
                        .ThenBy(w => w.Date)
                        .Select(w => new WarningViewModel(w))
                        .ToList();
                }

                // Pre-build staking VMs on background thread
                if (summary.StakingRewards.Count > 0)
                {
                    stakingAssetVms = summary.StakingRewards
                        .GroupBy(s => s.Asset, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new StakingAssetSummaryViewModel
                        {
                            Asset = g.Key,
                            Count = g.Count().ToString(),
                            TotalAmount = g.Sum(s => s.Amount),
                            TotalGbp = g.Sum(s => s.GbpValue)
                        })
                        .OrderByDescending(a => a.TotalGbp)
                        .ToList();

                    stakingVms = summary.StakingRewards
                        .OrderBy(s => s.Date)
                        .Select(s => new StakingViewModel(s))
                        .ToList();
                }

                // Pre-build asset P&L on background thread
                var totalAbsGain = summary.Disposals.Sum(d => Math.Abs(d.GainOrLoss));
                assetPnlVms = summary.Disposals
                    .GroupBy(d => d.Asset, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new AssetPnlViewModel
                    {
                        Asset = g.Key,
                        Count = g.Count().ToString(),
                        Proceeds = g.Sum(d => d.DisposalProceeds),
                        Cost = g.Sum(d => d.AllowableCost),
                        Gain = g.Sum(d => d.GainOrLoss),
                        TotalAbsGain = totalAbsGain
                    })
                    .OrderByDescending(a => Math.Abs(a.Gain))
                    .ToList();

            });

            // UI binding runs back on the UI thread — pass pre-built VMs to avoid work on UI thread
            LoadData(warningVms, stakingAssetVms, stakingVms, assetPnlVms);
            _isLoading = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ContentScroller.Visibility = Visibility.Visible;
        }
    }

    private void LoadData(
        List<WarningViewModel>? warningVms = null,
        List<StakingAssetSummaryViewModel>? stakingAssetVms = null,
        List<StakingViewModel>? stakingVms = null,
        List<AssetPnlViewModel>? assetPnlVms = null)
    {
        if (_summary == null) return;

        TaxYearTitle.Text = $"Tax Year {_summary.TaxYear}";
        TaxYearDateRange.Text = $"6 April {_summary.StartYear} to 5 April {_summary.StartYear + 1}";

        // Load user inputs
        var userInput = _mainWindow?.Settings.TaxYearInputs.GetValueOrDefault(_summary.TaxYear);
        TaxableIncomeBox.Value = userInput?.TaxableIncome > 0 ? (double)userInput.TaxableIncome : double.NaN;
        OtherGainsBox.Value = userInput?.OtherCapitalGains > 0 ? (double)userInput.OtherCapitalGains : double.NaN;

        UpdateSummaryDisplay();
        CheckFxRatesCoverage();
        LoadDeadlines();
        LoadSA108();
        LoadBnbReport();
        LoadBalances();
        LoadAssetPnl(assetPnlVms);
        LoadDisposals();
        LoadWarnings(warningVms);
        LoadStaking(stakingAssetVms, stakingVms);
        SetupWhatIf();
    }

    private void UpdateSummaryDisplay()
    {
        if (_summary == null) return;

        DisposalCountText.Text = _summary.Disposals.Count.ToString();
        TotalProceedsText.Text = FormatGbp(_summary.TotalDisposalProceeds);
        TotalCostsText.Text = FormatGbp(_summary.TotalAllowableCosts);
        TotalGainsText.Text = FormatGbp(_summary.TotalGains);
        TotalLossesText.Text = _summary.TotalLosses != 0 ? FormatGbp(_summary.TotalLosses) : "£0.00";
        NetGainText.Text = FormatGbp(_summary.NetGainOrLoss);
        AeaText.Text = FormatGbp(_summary.AnnualExemptAmount);
        TaxableGainText.Text = FormatGbp(_summary.TaxableGain);
        CgtDueText.Text = FormatGbp(_summary.CgtDue);

        LossesCarriedInText.Text = _summary.LossesCarriedIn > 0 ? FormatGbp(_summary.LossesCarriedIn) : "£0.00";
        LossesUsedText.Text = _summary.LossesUsedThisYear > 0 ? FormatGbp(_summary.LossesUsedThisYear) : "£0.00";
        LossesCarriedOutText.Text = _summary.LossesCarriedOut > 0 ? FormatGbp(_summary.LossesCarriedOut) : "£0.00";

        CgtRatesLabel.Text = $"Basic rate: {_summary.BasicRateCgt:P0} / Higher rate: {_summary.HigherRateCgt:P0}";

        BasicRateText.Text = $"CGT basic rate: {_summary.BasicRateCgt:P0}";
        HigherRateText.Text = $"CGT higher/additional rate: {_summary.HigherRateCgt:P0}";
        BasicBandText.Text = $"Basic rate band: {FormatGbp(_summary.BasicRateBand)}";
        PersonalAllowanceText.Text = $"Personal allowance: {FormatGbp(_summary.PersonalAllowance)}";
    }

    private void LoadDeadlines()
    {
        if (_summary == null) return;

        var endYear = _summary.StartYear + 1;
        var saOnlineDeadline = new DateTimeOffset(endYear + 1, 1, 31, 23, 59, 59, TimeSpan.Zero);
        var saPaperDeadline = new DateTimeOffset(endYear, 10, 31, 23, 59, 59, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        DeadlineSaText.Text = $"Self Assessment online filing & payment deadline: {saOnlineDeadline:dd MMMM yyyy}\n" +
                              $"Paper filing deadline: {saPaperDeadline:dd MMMM yyyy}";

        // Deadline notifications
        var daysToOnline = (saOnlineDeadline - now).TotalDays;
        var daysToPaper = (saPaperDeadline - now).TotalDays;

        string urgency = "";
        if (daysToOnline < 0)
            urgency = "OVERDUE: The online filing deadline has passed!";
        else if (daysToOnline <= 30)
            urgency = $"URGENT: Only {(int)daysToOnline} days until the online filing deadline.";
        else if (daysToOnline <= 90)
            urgency = $"REMINDER: {(int)daysToOnline} days until the online filing deadline.";
        else if (daysToPaper <= 30 && daysToPaper > 0)
            urgency = $"Paper filing deadline is in {(int)daysToPaper} days.";

        if (!string.IsNullOrEmpty(urgency))
        {
            DeadlineNoteText.Text = urgency + "\n\n" +
                "Note: For UK residential property disposals completed from 27 October 2021, you must report and pay CGT within 60 days of completion via the 'Report and pay Capital Gains Tax on UK property' service. Crypto assets are not residential property — the standard SA deadline applies.";
        }
        else
        {
            DeadlineNoteText.Text = "Note: For UK residential property disposals completed from 27 October 2021, you must report and pay CGT within 60 days of completion via the 'Report and pay Capital Gains Tax on UK property' service. Crypto assets are not residential property — the standard SA deadline applies.";
        }
    }

    private void LoadSA108()
    {
        if (_summary == null) return;

        Sa108DisposalCount.Text = _summary.Disposals.Count.ToString();
        Sa108Proceeds.Text = FormatGbp(_summary.TotalDisposalProceeds);
        Sa108Costs.Text = FormatGbp(_summary.TotalAllowableCosts);
        Sa108Gains.Text = FormatGbp(_summary.TotalGains);
        Sa108Losses.Text = _summary.TotalLosses != 0 ? FormatGbp(Math.Abs(_summary.TotalLosses)) : "£0.00";
        Sa108LossesUsed.Text = _summary.LossesUsedThisYear > 0 ? FormatGbp(_summary.LossesUsedThisYear) : "£0.00";
    }

    private void CopySA108_Click(object sender, RoutedEventArgs e)
    {
        if (_summary == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SA108 Capital Gains Summary — {_summary.TaxYear}");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"Box 3  Number of disposals:           {_summary.Disposals.Count}");
        sb.AppendLine($"Box 4  Disposal proceeds:             {FormatGbp(_summary.TotalDisposalProceeds)}");
        sb.AppendLine($"Box 5  Allowable costs:               {FormatGbp(_summary.TotalAllowableCosts)}");
        sb.AppendLine($"Box 6  Gains in the year:             {FormatGbp(_summary.TotalGains)}");
        sb.AppendLine($"Box 7  Losses in the year:            {FormatGbp(Math.Abs(_summary.TotalLosses))}");
        sb.AppendLine($"Box 8  Losses brought forward & used: {FormatGbp(_summary.LossesUsedThisYear)}");

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(sb.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private void LoadBnbReport()
    {
        if (_allBnbDisposals.Count == 0)
        {
            BnbPanel.Visibility = Visibility.Collapsed;
            return;
        }

        BnbPanel.Visibility = Visibility.Visible;
        BnbCountText.Text = $"{_allBnbDisposals.Count} disposal(s) matched under the Bed & Breakfast rule";

        var bnbAssets = _allBnbDisposals.Select(d => d.Asset).Distinct().OrderBy(a => a).ToList();
        BnbAssetFilter.ItemsSource = bnbAssets;
        BnbAssetFilter.SelectedIndex = -1;

        BnbList.ItemsSource = _allBnbDisposals;
        UpdateBnbFilterStatus();
    }

    private void BnbAssetFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyBnbFilter();
    }

    private void ClearBnbFilter_Click(object sender, RoutedEventArgs e)
    {
        BnbAssetFilter.SelectedIndex = -1;
        ApplyBnbFilter();
    }

    private void ApplyBnbFilter()
    {
        var selectedAsset = BnbAssetFilter.SelectedItem as string;
        BnbList.ItemsSource = string.IsNullOrEmpty(selectedAsset)
            ? _allBnbDisposals
            : _allBnbDisposals.Where(d => d.Asset.Equals(selectedAsset, StringComparison.OrdinalIgnoreCase)).ToList();
        UpdateBnbFilterStatus();
    }

    private void UpdateBnbFilterStatus()
    {
        var displayed = (BnbList.ItemsSource as IEnumerable<BnbDisposalViewModel>)?.Count() ?? 0;
        var total = _allBnbDisposals.Count;
        BnbFilterStatusText.Text = displayed == total
            ? $"Showing all {total} B&B disposal(s)"
            : $"Showing {displayed} of {total} B&B disposal(s)";
    }

    private static void ToggleSection(StackPanel content, Button button)
    {
        var collapsed = content.Visibility == Visibility.Collapsed;
        content.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        button.Content = collapsed ? "▲" : "▼";
    }

    private void WhatIfCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(WhatIfContent, WhatIfCollapseBtn);
    private void UserDetailsCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(UserDetailsContent, UserDetailsCollapseBtn);
    private void SummaryCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(SummaryContent, SummaryCollapseBtn);
    private void RatesCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(RatesContent, RatesCollapseBtn);
    private void StartBalanceCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(StartBalanceContent, StartBalanceCollapseBtn);
    private void EndBalanceCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(EndBalanceContent, EndBalanceCollapseBtn);
    private void WarningsCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(WarningsContent, WarningsCollapseBtn);
    private void StakingCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(StakingContent, StakingCollapseBtn);
    private void DeadlinesCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(DeadlinesContent, DeadlinesCollapseBtn);
    private void Sa108Collapse_Click(object sender, RoutedEventArgs e) => ToggleSection(Sa108Content, Sa108CollapseBtn);
    private void AssetPnlCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(AssetPnlContent, AssetPnlCollapseBtn);
    private void BnbCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(BnbContent, BnbCollapseBtn);

    private void LoadBalances()
    {
        if (_summary == null) return;

        // Start of year
        if (_summary.StartOfYearBalances.Balances.Count > 0)
        {
            StartBalancePanel.Visibility = Visibility.Visible;
            StartBalanceTitle.Text = _summary.StartOfYearBalances.Label;
            StartBalanceTotalText.Text = $"Total portfolio value: {FormatGbp(_summary.StartOfYearBalances.TotalGbpValue)}";
            StartBalanceList.ItemsSource = _summary.StartOfYearBalances.Balances;
        }
        else
        {
            StartBalancePanel.Visibility = Visibility.Collapsed;
        }

        // End of year
        if (_summary.EndOfYearBalances.Balances.Count > 0)
        {
            EndBalancePanel.Visibility = Visibility.Visible;
            EndBalanceTitle.Text = _summary.EndOfYearBalances.Label;
            EndBalanceTotalText.Text = $"Total portfolio value: {FormatGbp(_summary.EndOfYearBalances.TotalGbpValue)}";
            EndBalanceList.ItemsSource = _summary.EndOfYearBalances.Balances;
        }
        else
        {
            EndBalancePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadAssetPnl(List<AssetPnlViewModel>? preBuilt = null)
    {
        if (_summary == null) return;

        if (preBuilt != null)
        {
            AssetPnlList.ItemsSource = preBuilt;
            return;
        }

        var totalAbsGain = _summary.Disposals.Sum(d => Math.Abs(d.GainOrLoss));
        var assetPnl = _summary.Disposals
            .GroupBy(d => d.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AssetPnlViewModel
            {
                Asset = g.Key,
                Count = g.Count().ToString(),
                Proceeds = g.Sum(d => d.DisposalProceeds),
                Cost = g.Sum(d => d.AllowableCost),
                Gain = g.Sum(d => d.GainOrLoss),
                TotalAbsGain = totalAbsGain
            })
            .OrderByDescending(a => Math.Abs(a.Gain))
            .ToList();

        AssetPnlList.ItemsSource = assetPnl;
    }

    private void LoadDisposals()
    {
        if (_allDisposals == null) return;

        var assets = _allDisposals
            .Select(d => d.Asset)
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        DisposalAssetFilter.ItemsSource = assets;
        DisposalAssetFilter.SelectedIndex = -1;

        DisposalsList.ItemsSource = _allDisposals;
        UpdateFilterStatus();
    }

    private void DisposalsCollapse_Click(object sender, RoutedEventArgs e) => ToggleSection(DisposalsContent, DisposalsCollapseBtn);

    private void DisposalAssetFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDisposalFilter();
    }

    private void ClearDisposalFilter_Click(object sender, RoutedEventArgs e)
    {
        DisposalAssetFilter.SelectedIndex = -1;
        ApplyDisposalFilter();
    }

    private async void EditDisposal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DisposalViewModel vm)
            await ShowDisposalEditDialogAsync(vm);
    }

    private async Task ShowDisposalEditDialogAsync(DisposalViewModel vm)
    {
        if (_mainWindow == null) return;
        var record = vm.Record;
        var overrides = _mainWindow.Settings.CostBasisOverrides;
        var hasExisting = overrides.TryGetValue(record.TradeId, out var existingCost);

        var notes = _mainWindow.Settings.DisposalNotes;
        notes.TryGetValue(record.TradeId, out var existingNote);

        var costBox = new TextBox
        {
            Header = "Allowable Cost (GBP)",
            PlaceholderText = "e.g. 1500.00",
            Text = hasExisting ? existingCost.ToString("0.##") : record.AllowableCost.ToString("0.##")
        };

        var noteBox = new TextBox
        {
            Header = "Notes",
            PlaceholderText = "e.g. Transferred from Coinbase, original purchase price...",
            Text = existingNote ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 100
        };

        var info = new TextBlock
        {
            Text = $"{record.Asset} — {record.QuantityDisposed:0.########} disposed on {record.Date:dd/MM/yyyy}\n" +
                   $"Proceeds: £{record.DisposalProceeds:#,##0.00}  |  Calculated cost: £{record.AllowableCost:#,##0.00}  |  Rule: {record.MatchingRule}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(info);
        panel.Children.Add(costBox);
        panel.Children.Add(noteBox);

        var dialog = new ContentDialog
        {
            Title = "Disposal Details",
            Content = panel,
            PrimaryButtonText = "Save",
            SecondaryButtonText = hasExisting ? "Remove Cost Override" : "",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            // Save note
            var noteText = noteBox.Text?.Trim();
            if (!string.IsNullOrEmpty(noteText))
                notes[record.TradeId] = noteText;
            else
                notes.Remove(record.TradeId);

            // Save cost override
            if (decimal.TryParse(costBox.Text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var newCost))
            {
                overrides[record.TradeId] = newCost;
                _mainWindow.AddAuditEntry("Cost Override", $"{record.Asset} {record.Date:dd/MM/yyyy} — cost set to £{newCost:#,##0.00}");
            }

            await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
            await _mainWindow.RecalculateAndBuildTabsAsync();
        }
        else if (result == ContentDialogResult.Secondary && hasExisting)
        {
            overrides.Remove(record.TradeId);
            await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
            await _mainWindow.RecalculateAndBuildTabsAsync();
        }
    }

    private async void ExportHmrcSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null || _summary == null || _summary.Disposals.Count == 0) return;

        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = $"HMRC_Disposal_Schedule_{_summary.TaxYear.Replace("/", "-")}";
        picker.FileTypeChoices.Add("CSV File", new List<string> { ".csv" });

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var lines = new List<string>
        {
            "Asset Description,Date of Disposal (DD/MM/YYYY),Disposal Proceeds (GBP),Allowable Cost (GBP),Gain or Loss (GBP),Matching Rule"
        };

        foreach (var d in _summary.Disposals.OrderBy(d => d.Date))
        {
            lines.Add($"\"{d.QuantityDisposed:0.########} {d.Asset}\",{d.Date:dd/MM/yyyy},{d.DisposalProceeds:0.00},{d.AllowableCost:0.00},{d.GainOrLoss:0.00},{d.MatchingRule}");
        }

        lines.Add("");
        lines.Add($"Total Disposal Proceeds,,{_summary.TotalDisposalProceeds:0.00}");
        lines.Add($"Total Allowable Costs,,,{_summary.TotalAllowableCosts:0.00}");
        lines.Add($"Total Gains,,,,{_summary.TotalGains:0.00}");
        lines.Add($"Total Losses,,,,{_summary.TotalLosses:0.00}");
        lines.Add($"Net Gain/Loss,,,,{_summary.NetGainOrLoss:0.00}");

        await System.IO.File.WriteAllLinesAsync(file.Path, lines);
    }

    private void ApplyDisposalFilter()
    {
        if (_allDisposals == null) return;

        var selectedAsset = DisposalAssetFilter.SelectedItem as string;

        if (string.IsNullOrEmpty(selectedAsset))
        {
            DisposalsList.ItemsSource = _allDisposals;
        }
        else
        {
            var filtered = _allDisposals
                .Where(d => d.Asset.Equals(selectedAsset, StringComparison.OrdinalIgnoreCase))
                .ToList();
            DisposalsList.ItemsSource = filtered;
        }

        UpdateFilterStatus();
    }

    private void UpdateFilterStatus()
    {
        if (_allDisposals == null) return;

        var displayedCount = (DisposalsList.ItemsSource as IEnumerable<DisposalViewModel>)?.Count() ?? 0;
        var totalCount = _allDisposals.Count;

        if (displayedCount == totalCount)
        {
            FilterStatusText.Text = $"Showing all {totalCount} disposals";
        }
        else
        {
            FilterStatusText.Text = $"Showing {displayedCount} of {totalCount} disposals";
        }
    }

    private async void TaxableIncome_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading || _mainWindow == null || _summary == null) return;

        var income = double.IsNaN(args.NewValue) ? 0m : (decimal)args.NewValue;
        EnsureUserInput().TaxableIncome = income;

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        await _mainWindow.RecalculateSummariesOnlyAsync();

        var updated = _mainWindow.TaxYearSummaries.FirstOrDefault(s => s.TaxYear == _summary.TaxYear);
        if (updated != null)
        {
            _summary = updated;
            UpdateSummaryDisplay();
        }
    }

    private async void OtherGains_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading || _mainWindow == null || _summary == null) return;

        var gains = double.IsNaN(args.NewValue) ? 0m : (decimal)args.NewValue;
        EnsureUserInput().OtherCapitalGains = gains;

        await _mainWindow.StorageService.SaveSettingsAsync(_mainWindow.Settings);
        await _mainWindow.RecalculateSummariesOnlyAsync();

        var updated = _mainWindow.TaxYearSummaries.FirstOrDefault(s => s.TaxYear == _summary.TaxYear);
        if (updated != null)
        {
            _summary = updated;
            UpdateSummaryDisplay();
        }
    }

    private void LoadWarnings(List<WarningViewModel>? preBuilt = null)
    {
        if (_summary == null || _summary.Warnings.Count == 0)
        {
            WarningsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        WarningsPanel.Visibility = Visibility.Visible;
        var errorCount = _summary.Warnings.Count(w => w.Level == WarningLevel.Error);
        var warnCount = _summary.Warnings.Count(w => w.Level == WarningLevel.Warning);
        WarningsTitle.Text = $"Data Issues ({errorCount} errors, {warnCount} warnings, {_summary.Warnings.Count - errorCount - warnCount} info)";

        WarningsList.ItemsSource = preBuilt ?? _summary.Warnings
            .OrderByDescending(w => w.Level)
            .ThenBy(w => w.Date)
            .Select(w => new WarningViewModel(w))
            .ToList();
    }

    private void CopyWarnings_Click(object sender, RoutedEventArgs e)
    {
        if (_summary?.Warnings == null || _summary.Warnings.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Data Issues — {_summary.TaxYear}");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine();

        foreach (var w in _summary.Warnings.OrderByDescending(w => w.Level).ThenBy(w => w.Date))
        {
            var level = w.Level == WarningLevel.Error ? "ERROR  " : w.Level == WarningLevel.Warning ? "WARNING" : "INFO   ";
            var date = w.Date.HasValue ? w.Date.Value.ToString("dd/MM/yyyy HH:mm") : "          ";
            sb.AppendLine($"[{level}] {w.Category,-10} {date}  {w.Message}");
        }

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(sb.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private void LoadStaking(List<StakingAssetSummaryViewModel>? preBuiltAssets = null, List<StakingViewModel>? preBuiltRewards = null)
    {
        if (_summary == null || _summary.StakingRewards.Count == 0)
        {
            StakingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StakingPanel.Visibility = Visibility.Visible;
        StakingTotalText.Text = $"Total staking/dividend income: {FormatGbp(_summary.StakingIncome)}";

        StakingByAssetList.ItemsSource = preBuiltAssets ?? _summary.StakingRewards
            .GroupBy(s => s.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g => new StakingAssetSummaryViewModel
            {
                Asset = g.Key,
                Count = g.Count().ToString(),
                TotalAmount = g.Sum(s => s.Amount),
                TotalGbp = g.Sum(s => s.GbpValue)
            })
            .OrderByDescending(a => a.TotalGbp)
            .ToList();

        StakingList.ItemsSource = preBuiltRewards ?? _summary.StakingRewards
            .OrderBy(s => s.Date)
            .Select(s => new StakingViewModel(s))
            .ToList();
    }

    private async void ExportStakingReport_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null || _summary == null || _summary.StakingRewards.Count == 0) return;

        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = $"Staking_Income_{_summary.TaxYear.Replace("/", "-")}";
        picker.FileTypeChoices.Add("CSV File", new List<string> { ".csv" });

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var lines = new List<string>
        {
            "Date,Asset,Amount,GBP Value"
        };

        foreach (var r in _summary.StakingRewards.OrderBy(r => r.Date))
            lines.Add($"{r.Date:dd/MM/yyyy},{r.Asset},{r.Amount:0.########},{r.GbpValue:0.00}");

        lines.Add("");
        lines.Add($"Total,,, {_summary.StakingIncome:0.00}");

        await System.IO.File.WriteAllLinesAsync(file.Path, lines);
    }

    private TaxYearUserInput EnsureUserInput()
    {
        if (_mainWindow == null || _summary == null)
            return new TaxYearUserInput();

        if (!_mainWindow.Settings.TaxYearInputs.TryGetValue(_summary.TaxYear, out var input))
        {
            input = new TaxYearUserInput();
            _mainWindow.Settings.TaxYearInputs[_summary.TaxYear] = input;
        }
        return input;
    }

    // ========== What-If Scenario ==========

    private readonly List<WhatIfTrade> _whatIfTrades = new();

    private void SetupWhatIf()
    {
        if (_summary == null) return;

        // Show what-if panel only for tax years that haven't ended yet
        var endYear = _summary.StartYear + 1;
        var taxYearEnd = new DateTimeOffset(endYear, 4, 5, 23, 59, 59, TimeSpan.Zero);
        WhatIfPanel.Visibility = DateTimeOffset.Now <= taxYearEnd ? Visibility.Visible : Visibility.Collapsed;

        // Default the date picker to today
        WhatIfDatePicker.Date = DateTimeOffset.Now;
        UpdateWhatIfTradesList();
    }

    private void WhatIfAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null || _summary == null || _mainWindow.FxService == null) return;

        var asset = WhatIfAssetBox.Text.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(asset))
        {
            ShowWhatIfError("Enter an asset name (e.g. XRP, BTC, ETH).");
            return;
        }

        var price = WhatIfPriceBox.Value;
        var quantity = WhatIfQuantityBox.Value;
        if (double.IsNaN(price) || price <= 0 || double.IsNaN(quantity) || quantity <= 0)
        {
            ShowWhatIfError("Enter a valid price and quantity.");
            return;
        }

        if (!WhatIfDatePicker.Date.HasValue)
        {
            ShowWhatIfError("Select a sale date.");
            return;
        }

        var saleDate = new DateTimeOffset(WhatIfDatePicker.Date.Value.DateTime, TimeSpan.Zero);
        var saleTaxYear = CgtCalculationService.GetTaxYearLabel(saleDate);
        if (saleTaxYear != _summary.TaxYear)
        {
            ShowWhatIfError($"The date {saleDate:dd/MM/yyyy} falls in tax year {saleTaxYear}, not {_summary.TaxYear}.");
            return;
        }

        var currency = (WhatIfCurrencyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "USD";
        var isBuy = (WhatIfSideBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Buy";

        // 30-day repurchase alert: if this is a Buy, check if there are recent sales
        // of the same asset within 30 days prior that would trigger B&B matching
        if (isBuy && _mainWindow != null)
        {
            var normAsset = KrakenLedgerEntry.NormaliseAssetName(asset);
            var allDisposals = _mainWindow.TaxYearSummaries
                .SelectMany(s => s.Disposals)
                .Where(d => string.Equals(d.Asset, normAsset, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Also check existing what-if sells
            var recentSales = allDisposals
                .Where(d => d.Date >= saleDate.AddDays(-30) && d.Date <= saleDate)
                .ToList();

            // Check pending what-if sells too
            var pendingWhatIfSales = _whatIfTrades
                .Where(t => !t.IsBuy && string.Equals(t.Asset, normAsset, StringComparison.OrdinalIgnoreCase)
                    && t.Date >= saleDate.AddDays(-30) && t.Date <= saleDate)
                .ToList();

            if (recentSales.Count > 0 || pendingWhatIfSales.Count > 0)
            {
                WhatIfInfoBar.Message = $"Warning: Buying {normAsset} within 30 days of a sale will trigger Bed & Breakfast matching (TCGA 1992 s106A). " +
                    $"The buy will be matched to the earlier sale, potentially changing its cost basis.";
                WhatIfInfoBar.Severity = InfoBarSeverity.Warning;
                WhatIfInfoBar.IsOpen = true;
            }
        }

        _whatIfTrades.Add(new WhatIfTrade
        {
            Asset = asset,
            Currency = currency,
            Price = (decimal)price,
            Quantity = (decimal)quantity,
            Date = saleDate,
            IsBuy = isBuy
        });

        // Clear inputs for next trade
        WhatIfAssetBox.Text = "";
        WhatIfPriceBox.Value = double.NaN;
        WhatIfQuantityBox.Value = double.NaN;
        WhatIfSideBox.SelectedIndex = 0;
        WhatIfInfoBar.IsOpen = false;
        WhatIfResultsPanel.Visibility = Visibility.Collapsed;

        UpdateWhatIfTradesList();
    }

    private void WhatIfRemoveTrade_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index && index >= 0 && index < _whatIfTrades.Count)
        {
            _whatIfTrades.RemoveAt(index);
            WhatIfResultsPanel.Visibility = Visibility.Collapsed;
            UpdateWhatIfTradesList();
        }
    }

    private void UpdateWhatIfTradesList()
    {
        var hasTrades = _whatIfTrades.Count > 0;
        WhatIfTradesPanel.Visibility = hasTrades ? Visibility.Visible : Visibility.Collapsed;
        WhatIfCalculateBtn.IsEnabled = hasTrades;
        WhatIfTradesHeader.Text = $"Hypothetical Trades ({_whatIfTrades.Count})";

        WhatIfTradesList.ItemsSource = _whatIfTrades
            .Select((t, i) => new WhatIfTradeViewModel(t, i))
            .ToList();
    }

    private void WhatIfCalculate_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null || _summary == null || _mainWindow.FxService == null) return;
        if (_whatIfTrades.Count == 0) return;

        // Clone the ledger
        var tempLedger = new List<KrakenLedgerEntry>(_mainWindow.Ledger);
        var syntheticRefIds = new List<string>();
        var tradeDescriptions = new List<string>();

        foreach (var trade in _whatIfTrades)
        {
            // Convert sale proceeds to GBP
            decimal proceedsInCurrency = trade.Price * trade.Quantity;
            decimal proceedsGbp;
            if (trade.Currency == "GBP")
                proceedsGbp = proceedsInCurrency;
            else
                proceedsGbp = _mainWindow.FxService.ConvertToGbp(proceedsInCurrency, trade.Currency, trade.Date);

            if (proceedsGbp <= 0)
            {
                ShowWhatIfError($"Could not convert {trade.Currency} to GBP for {trade.Asset} trade on {trade.Date:dd/MM/yyyy}. Download FX rates first.");
                return;
            }

            var unixTime = trade.Date.ToUnixTimeSeconds();
            var syntheticRefId = "WHATIF-" + Guid.NewGuid().ToString("N")[..8];
            syntheticRefIds.Add(syntheticRefId);

            // Buy: +crypto, -fiat. Sell: -crypto, +fiat.
            var cryptoAmount = trade.IsBuy ? trade.Quantity : -trade.Quantity;
            var fiatAmount = trade.IsBuy ? -proceedsInCurrency : proceedsInCurrency;

            tempLedger.Add(new KrakenLedgerEntry
            {
                RefId = syntheticRefId,
                Time = unixTime,
                Type = "trade",
                SubType = "tradespot",
                Asset = trade.Asset,
                AmountStr = cryptoAmount.ToString(),
                FeeStr = "0",
                LedgerId = "WHATIF-CRYPTO",
                NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName(trade.Asset)
            });

            tempLedger.Add(new KrakenLedgerEntry
            {
                RefId = syntheticRefId,
                Time = unixTime,
                Type = "trade",
                SubType = "tradespot",
                Asset = trade.Currency,
                AmountStr = fiatAmount.ToString(),
                FeeStr = "0",
                LedgerId = "WHATIF-FIAT",
                NormalisedAsset = trade.Currency
            });

            var side = trade.IsBuy ? "Buy" : "Sell";
            var costOrProceeds = trade.IsBuy ? "cost" : "proceeds";
            tradeDescriptions.Add($"{side} {trade.Quantity:#,##0.########} {trade.Asset} at {trade.Price:#,##0.########} {trade.Currency}/unit on {trade.Date:dd/MM/yyyy} — {costOrProceeds} {FormatGbp(proceedsGbp)}");
        }

        // Run the full CGT calculation with all hypothetical trades
        var tempWarnings = new List<CalculationWarning>();
        var tempCgtService = new CgtCalculationService(_mainWindow.FxService, tempWarnings, _mainWindow.Trades, _mainWindow.Settings.EffectiveDelistedAssets, _mainWindow.Settings.CostBasisOverrides);
        var tempSummaries = tempCgtService.CalculateAllTaxYears(tempLedger, _mainWindow.Settings.TaxYearInputs);

        var whatIfSummary = tempSummaries.FirstOrDefault(s => s.TaxYear == _summary.TaxYear);
        if (whatIfSummary == null)
        {
            ShowWhatIfError("Could not calculate what-if scenario.");
            return;
        }

        // Display comparison
        WhatIfInfoBar.IsOpen = false;
        WhatIfResultsPanel.Visibility = Visibility.Visible;

        WhatIfTradeDescription.Text = string.Join("\n", tradeDescriptions);

        // Show disposal details for each what-if trade
        var disposalDetails = new List<string>();
        foreach (var refId in syntheticRefIds)
        {
            var disposal = whatIfSummary.Disposals.FirstOrDefault(d => d.TradeId == refId);
            if (disposal != null)
                disposalDetails.Add($"{disposal.Asset}: Proceeds {FormatGbp(disposal.DisposalProceeds)}, Cost {FormatGbp(disposal.AllowableCost)}, Gain {FormatGbp(disposal.GainOrLoss)}");
        }

        // Cross-year impact (Feature 17): show effect on other tax years too
        var crossYearEffects = new List<string>();
        foreach (var newSummary in tempSummaries.OrderBy(s => s.StartYear))
        {
            var origSummary = _mainWindow.TaxYearSummaries.FirstOrDefault(s => s.TaxYear == newSummary.TaxYear);
            if (origSummary == null) continue;
            var cgtDiff = newSummary.CgtDue - origSummary.CgtDue;
            var lossCarryDiff = newSummary.LossesCarriedOut - origSummary.LossesCarriedOut;
            if (Math.Abs(cgtDiff) > 0.005m || Math.Abs(lossCarryDiff) > 0.005m)
            {
                var parts = new List<string>();
                if (Math.Abs(cgtDiff) > 0.005m)
                    parts.Add($"CGT {FormatDiff(cgtDiff)}");
                if (Math.Abs(lossCarryDiff) > 0.005m)
                    parts.Add($"Losses c/f {FormatDiff(lossCarryDiff)}");
                crossYearEffects.Add($"  {newSummary.TaxYear}: {string.Join(", ", parts)}");
            }
        }

        if (crossYearEffects.Count > 0)
            disposalDetails.Add("\nCross-year impact:\n" + string.Join("\n", crossYearEffects));

        WhatIfTradeSummary.Text = disposalDetails.Count > 0 ? string.Join("\n", disposalDetails) : "";

        var currentNet = _summary.TotalGains + _summary.TotalLosses;
        var newNet = whatIfSummary.TotalGains + whatIfSummary.TotalLosses;

        WhatIfCurrentNet.Text = FormatGbp(currentNet);
        WhatIfNewNet.Text = FormatGbp(newNet);
        WhatIfDiffNet.Text = FormatDiff(newNet - currentNet);

        WhatIfCurrentTaxable.Text = FormatGbp(_summary.TaxableGain);
        WhatIfNewTaxable.Text = FormatGbp(whatIfSummary.TaxableGain);
        WhatIfDiffTaxable.Text = FormatDiff(whatIfSummary.TaxableGain - _summary.TaxableGain);

        WhatIfCurrentCgt.Text = FormatGbp(_summary.CgtDue);
        WhatIfNewCgt.Text = FormatGbp(whatIfSummary.CgtDue);
        WhatIfDiffCgt.Text = FormatDiff(whatIfSummary.CgtDue - _summary.CgtDue);
        WhatIfDiffCgt.Foreground = whatIfSummary.CgtDue > _summary.CgtDue
            ? new SolidColorBrush(Colors.Red)
            : new SolidColorBrush(Colors.Green);

        // Show total CGT across ALL years
        var totalCurrentCgt = _mainWindow.TaxYearSummaries.Sum(s => s.CgtDue);
        var totalNewCgt = tempSummaries.Sum(s => s.CgtDue);
        var totalDiff = totalNewCgt - totalCurrentCgt;
        if (Math.Abs(totalDiff - (whatIfSummary.CgtDue - _summary.CgtDue)) > 0.005m)
        {
            WhatIfDiffCgt.Text += $" (all years: {FormatDiff(totalDiff)})";
        }
    }

    private void WhatIfClear_Click(object sender, RoutedEventArgs e)
    {
        WhatIfAssetBox.Text = "";
        WhatIfPriceBox.Value = double.NaN;
        WhatIfQuantityBox.Value = double.NaN;
        WhatIfDatePicker.Date = DateTimeOffset.Now;
        WhatIfCurrencyBox.SelectedIndex = 0;
        WhatIfSideBox.SelectedIndex = 0;
        _whatIfTrades.Clear();
        UpdateWhatIfTradesList();
        WhatIfResultsPanel.Visibility = Visibility.Collapsed;
        WhatIfInfoBar.IsOpen = false;
    }

    private void ShowWhatIfError(string message)
    {
        WhatIfInfoBar.Message = message;
        WhatIfInfoBar.Severity = InfoBarSeverity.Warning;
        WhatIfInfoBar.IsOpen = true;
        WhatIfResultsPanel.Visibility = Visibility.Collapsed;
    }

    private static string FormatDiff(decimal diff)
    {
        if (diff == 0) return "no change";
        return diff < 0
            ? $"-£{Math.Abs(diff):#,##0.00}"
            : $"+£{diff:#,##0.00}";
    }

    // ========== Export handlers ==========

    private readonly ExportService _exportService = new();

    private List<KrakenTrade>? GetKrakenTradesIfChecked()
    {
        return IncludeKrakenData.IsChecked == true ? _mainWindow?.Trades : null;
    }

    private async void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (_summary == null) return;
        var path = await PickSaveFileAsync("Excel Workbook", ".xlsx", $"CGT_{_summary.TaxYear.Replace("/", "-")}.xlsx");
        if (path == null) return;

        try
        {
            _exportService.ExportToExcel(path, _summary, GetKrakenTradesIfChecked());
            ShowExportSuccess(path);
        }
        catch (Exception ex) { ShowExportError(ex); }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_summary == null) return;
        var path = await PickSaveFileAsync("PDF Document", ".pdf", $"CGT_{_summary.TaxYear.Replace("/", "-")}.pdf");
        if (path == null) return;

        try
        {
            _exportService.ExportToPdf(path, _summary, GetKrakenTradesIfChecked());
            ShowExportSuccess(path);
        }
        catch (Exception ex) { ShowExportError(ex); }
    }

    private async void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        if (_summary == null) return;
        var path = await PickSaveFileAsync("Word Document", ".docx", $"CGT_{_summary.TaxYear.Replace("/", "-")}.docx");
        if (path == null) return;

        try
        {
            _exportService.ExportToWord(path, _summary, GetKrakenTradesIfChecked());
            ShowExportSuccess(path);
        }
        catch (Exception ex) { ShowExportError(ex); }
    }

    private async void ExportSa108Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (_summary == null) return;
        var path = await PickSaveFileAsync("PDF Document", ".pdf", $"SA108_{_summary.TaxYear.Replace("/", "-")}.pdf");
        if (path == null) return;

        try
        {
            _exportService.ExportSa108ToPdf(path, _summary);
            ShowExportSuccess(path);
        }
        catch (Exception ex) { ShowExportError(ex); }
    }

    private async void ExportAccountantReport_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        var path = await PickSaveFileAsync("PDF Document", ".pdf", "Accountant_Report.pdf");
        if (path == null) return;

        try
        {
            _exportService.ExportAccountantReport(path, _mainWindow.TaxYearSummaries,
                _mainWindow.FinalPools, _mainWindow.Warnings);
            ShowExportSuccess(path);
        }
        catch (Exception ex) { ShowExportError(ex); }
    }

    private async void ExportAllExcel_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;
        var path = await PickSaveFileAsync("Excel Workbook", ".xlsx", "CGT_AllYears.xlsx");
        if (path == null) return;

        try
        {
            _exportService.ExportAllYearsToExcel(path, _mainWindow.TaxYearSummaries, GetKrakenTradesIfChecked());
            ShowExportSuccess(path);
        }
        catch (Exception ex) { ShowExportError(ex); }
    }

    private async System.Threading.Tasks.Task<string?> PickSaveFileAsync(string fileTypeLabel, string extension, string suggestedName)
    {
        var picker = new FileSavePicker();

        // WinUI 3 requires setting the window handle
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = suggestedName;
        picker.FileTypeChoices.Add(fileTypeLabel, new List<string> { extension });

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private void ShowExportSuccess(string path)
    {
        ExportInfoBar.Message = $"Exported to {path}";
        ExportInfoBar.Severity = InfoBarSeverity.Success;
        ExportInfoBar.IsOpen = true;
    }

    private void ShowExportError(Exception ex)
    {
        ExportInfoBar.Message = $"Export failed: {ex.Message}";
        ExportInfoBar.Severity = InfoBarSeverity.Error;
        ExportInfoBar.IsOpen = true;
    }

    private void CheckFxRatesCoverage()
    {
        if (_summary == null || _mainWindow?.FxService == null) return;

        var taxYearEnd = new DateTimeOffset(_summary.StartYear + 1, 4, 5, 23, 59, 59, TimeSpan.Zero);

        // Get all currencies used in disposals for this tax year
        var currencies = _summary.Disposals
            .Select(d => d.Asset)
            .Where(a => !string.IsNullOrEmpty(a) && !a.Equals("GBP", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currencies.Count == 0) return;

        // Check if we have FX rates coverage up to the tax year end
        var latestAvailableDate = GetLatestFxRateDate(currencies);

        if (latestAvailableDate.HasValue && latestAvailableDate.Value.Date < taxYearEnd.Date)
        {
            var daysMissing = (taxYearEnd.Date - latestAvailableDate.Value.Date).Days;

            // Try to use the FxRatesWarningBar if it exists, otherwise fall back to ExportInfoBar
            try
            {
                FxRatesWarningBar.Message = $"FX rates only available until {latestAvailableDate.Value:dd MMM yyyy}. " +
                                           $"Missing {daysMissing} days including tax year end (5 April {taxYearEnd.Year}). " +
                                           "This may affect the accuracy of end-of-year holdings calculations.";
                FxRatesWarningBar.IsOpen = true;
            }
            catch
            {
                // Fallback to ExportInfoBar if FxRatesWarningBar is not available
                ExportInfoBar.Message = $"FX rates only available until {latestAvailableDate.Value:dd MMM yyyy}. " +
                                       $"Missing {daysMissing} days including tax year end (5 April {taxYearEnd.Year}).";
                ExportInfoBar.Severity = InfoBarSeverity.Warning;
                ExportInfoBar.IsOpen = true;
            }
        }
        else
        {
            try
            {
                FxRatesWarningBar.IsOpen = false;
            }
            catch
            {
                // Ignore if control doesn't exist
            }
        }
    }

    private DateTimeOffset? GetLatestFxRateDate(List<string> currencies)
    {
        if (_mainWindow?.FxService == null) return null;

        DateTimeOffset? latestDate = null;

        foreach (var currency in currencies)
        {
            try
            {
                // Try a recent date to see if we have rates
                var testDate = DateTimeOffset.UtcNow.Date.AddDays(-1); // Start from yesterday

                // Work backwards to find the latest date with available rates (check up to 30 days)
                for (int i = 0; i < 30; i++)
                {
                    var checkDate = testDate.AddDays(-i);
                    var rate = _mainWindow.FxService.ConvertToGbp(1m, currency, checkDate);

                    if (rate > 0) // Found a valid rate
                    {
                        if (!latestDate.HasValue || checkDate > latestDate.Value)
                        {
                            latestDate = checkDate;
                        }
                        break;
                    }
                }
            }
            catch
            {
                // Ignore errors for individual currencies
            }
        }

        return latestDate;
    }

    private async void DownloadFxRates_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        try
        {
            // Navigate to settings page
            _mainWindow.NavigateToSettings();
        }
        catch (Exception ex)
        {
            ExportInfoBar.Message = $"Failed to navigate to settings: {ex.Message}";
            ExportInfoBar.Severity = InfoBarSeverity.Error;
            ExportInfoBar.IsOpen = true;
        }
    }

    private static string FormatGbp(decimal amount)
    {
        return amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
    }
}

public class DisposalViewModel
{
    private static readonly SolidColorBrush GreenBrush = new(Colors.Green);
    private static readonly SolidColorBrush RedBrush = new(Colors.Red);
    private static readonly SolidColorBrush OrangeBrush = new(Colors.Orange);
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.White) { Opacity = 0 };

    private readonly DisposalRecord _record;
    private readonly bool _hasOverride;
    private readonly bool _hasNote;
    private Func<string, List<BnbLedgerEntryViewModel>>? _entryResolver;
    private List<BnbLedgerEntryViewModel>? _disposalEntries;
    private List<BnbLedgerEntryViewModel>? _acquisitionEntries;

    public DisposalViewModel(DisposalRecord record, bool hasOverride = false, bool hasNote = false,
        Func<string, List<BnbLedgerEntryViewModel>>? entryResolver = null)
    {
        _record = record;
        _hasOverride = hasOverride;
        _hasNote = hasNote;
        _entryResolver = entryResolver;
    }

    public DisposalRecord Record => _record;
    public string DateFormatted => _record.Date.ToString("dd/MM/yyyy");
    public string Asset => _record.Asset;
    public string QuantityFormatted => _record.QuantityDisposed.ToString("0.########");
    public string ProceedsFormatted => FormatGbp(_record.DisposalProceeds);
    public string CostFormatted => _hasOverride ? $"{FormatGbp(_record.AllowableCost)} *" : FormatGbp(_record.AllowableCost);
    public string GainFormatted => FormatGbp(_record.GainOrLoss);
    public string MatchingRule => _record.MatchingRule;
    public SolidColorBrush GainColor => _record.GainOrLoss >= 0 ? GreenBrush : RedBrush;
    public SolidColorBrush CostColor => _hasOverride ? OrangeBrush : TransparentBrush;
    public string NoteIndicator => _hasNote ? "[note]" : "";

    public List<BnbLedgerEntryViewModel> DisposalEntries
    {
        get
        {
            if (_disposalEntries == null)
            {
                _disposalEntries = _entryResolver?.Invoke(_record.TradeId) ?? [];
            }
            return _disposalEntries;
        }
    }

    public List<BnbLedgerEntryViewModel> AcquisitionEntries
    {
        get
        {
            if (_acquisitionEntries == null)
            {
                _acquisitionEntries = _entryResolver?.Invoke(_record.AcquisitionRefId ?? "") ?? [];
            }
            return _acquisitionEntries;
        }
    }

    public Visibility AcquisitionSectionVisibility =>
        !string.IsNullOrEmpty(_record.AcquisitionRefId) ? Visibility.Visible : Visibility.Collapsed;
    public string AcquisitionDateLabel
    {
        get
        {
            if (AcquisitionEntries.Count > 0)
                return $"Reacquired on {AcquisitionEntries.Min(e => e.RawDateTime):dd/MM/yyyy}";
            return "";
        }
    }

    private static string FormatGbp(decimal amount)
    {
        return amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
    }
}

public class WarningViewModel
{
    private static readonly SolidColorBrush RedBrush = new(Colors.Red);
    private static readonly SolidColorBrush OrangeBrush = new(Colors.Orange);
    private static readonly SolidColorBrush GrayBrush = new(Colors.Gray);

    private readonly CalculationWarning _warning;

    public WarningViewModel(CalculationWarning warning) => _warning = warning;

    public string LevelText => _warning.Level.ToString().ToUpper();
    public string Category => _warning.Category;
    public string DateFormatted => _warning.DateFormatted;
    public string Message => _warning.Message;
    public SolidColorBrush LevelColor => _warning.Level switch
    {
        WarningLevel.Error => RedBrush,
        WarningLevel.Warning => OrangeBrush,
        _ => GrayBrush
    };
}

public class StakingViewModel
{
    private readonly StakingReward _reward;

    public StakingViewModel(StakingReward reward) => _reward = reward;

    public string DateFormatted => _reward.DateFormatted;
    public string Asset => _reward.Asset;
    public string AmountFormatted => _reward.Amount.ToString("0.########");
    public string GbpFormatted => _reward.GbpValue < 0
        ? $"-£{Math.Abs(_reward.GbpValue):#,##0.00}"
        : $"£{_reward.GbpValue:#,##0.00}";
}

public class WhatIfTrade
{
    public string Asset { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset Date { get; set; }
    public bool IsBuy { get; set; }
}

public class WhatIfTradeViewModel
{
    private readonly WhatIfTrade _trade;

    public WhatIfTradeViewModel(WhatIfTrade trade, int index)
    {
        _trade = trade;
        Index = index;
    }

    public int Index { get; }
    public string Description
    {
        get
        {
            var side = _trade.IsBuy ? "Buy" : "Sell";
            return $"{side} {_trade.Quantity:#,##0.########} {_trade.Asset} at {_trade.Price:#,##0.########} {_trade.Currency} on {_trade.Date:dd/MM/yyyy}";
        }
    }
}

public class StakingAssetSummaryViewModel
{
    public string Asset { get; set; } = "";
    public string Count { get; set; } = "0";
    public decimal TotalAmount { get; set; }
    public decimal TotalGbp { get; set; }

    public string TotalAmountFormatted => TotalAmount.ToString("0.########");
    public string TotalGbpFormatted => TotalGbp < 0
        ? $"-£{Math.Abs(TotalGbp):#,##0.00}"
        : $"£{TotalGbp:#,##0.00}";
}

public class AssetPnlViewModel
{
    private static readonly SolidColorBrush GreenBrush = new(Colors.Green);
    private static readonly SolidColorBrush RedBrush = new(Colors.Red);

    public string Asset { get; set; } = "";
    public string Count { get; set; } = "0";
    public decimal Proceeds { get; set; }
    public decimal Cost { get; set; }
    public decimal Gain { get; set; }
    public decimal TotalAbsGain { get; set; }

    public string ProceedsFormatted => FormatGbp(Proceeds);
    public string CostFormatted => FormatGbp(Cost);
    public string GainFormatted => FormatGbp(Gain);
    public string PercentFormatted => TotalAbsGain > 0
        ? $"{Math.Abs(Gain) / TotalAbsGain * 100:0.0}%"
        : "";
    public SolidColorBrush GainColor => Gain >= 0 ? GreenBrush : RedBrush;

    private static string FormatGbp(decimal amount)
    {
        return amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
    }
}

public class BnbLedgerEntryViewModel
{
    private readonly KrakenLedgerEntry _entry;
    private readonly string _gbpRateFormatted;
    private readonly string _rateDateFormatted;

    public BnbLedgerEntryViewModel(KrakenLedgerEntry entry, Services.FxConversionService? fxService = null)
    {
        _entry = entry;

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

    public DateTimeOffset RawDateTime => _entry.DateTime;
    public string DateFormatted => _entry.DateTime.ToString("dd/MM/yyyy HH:mm");
    public string Type => string.IsNullOrEmpty(_entry.SubType)
        ? _entry.Type
        : $"{_entry.Type}/{_entry.SubType}";
    public string Asset => string.IsNullOrEmpty(_entry.NormalisedAsset) ? _entry.Asset : _entry.NormalisedAsset;
    public string AmountFormatted => _entry.Amount >= 0
        ? $"+{_entry.Amount:0.########}"
        : _entry.Amount.ToString("0.########");
    public string FeeFormatted => _entry.Fee != 0 ? $"{_entry.Fee:0.########}" : "";
    public string LedgerId => _entry.LedgerId;
    public string GbpRateFormatted => _gbpRateFormatted;
    public string RateDateFormatted => _rateDateFormatted;
}

public class BnbDisposalViewModel
{
    private static readonly SolidColorBrush GreenBrush = new(Colors.Green);
    private static readonly SolidColorBrush RedBrush = new(Colors.Red);

    private readonly DisposalRecord _record;

    public BnbDisposalViewModel(DisposalRecord record,
        List<BnbLedgerEntryViewModel> disposalEntries,
        List<BnbLedgerEntryViewModel> acquisitionEntries)
    {
        _record = record;
        DisposalEntries = disposalEntries;
        AcquisitionEntries = acquisitionEntries;
    }

    public string DateFormatted => _record.Date.ToString("dd/MM/yyyy");
    public string Asset => _record.Asset;
    public string QuantityFormatted => _record.QuantityDisposed.ToString("0.########");
    public string ProceedsFormatted => FormatGbp(_record.DisposalProceeds);
    public string CostFormatted => FormatGbp(_record.AllowableCost);
    public string GainFormatted => FormatGbp(_record.GainOrLoss);
    public SolidColorBrush GainColor => _record.GainOrLoss >= 0 ? GreenBrush : RedBrush;

    public List<BnbLedgerEntryViewModel> DisposalEntries { get; }
    public List<BnbLedgerEntryViewModel> AcquisitionEntries { get; }

    public string AcquisitionDateLabel => AcquisitionEntries.Count > 0
        ? $"Reacquired on {AcquisitionEntries.Min(e => e.RawDateTime):dd/MM/yyyy}"
        : "No reacquisition entries found in ledger (data may predate this feature)";

    private static string FormatGbp(decimal amount)
        => amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
}
