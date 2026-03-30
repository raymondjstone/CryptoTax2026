using System;
using System.Collections.Generic;
using System.Linq;
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
    private List<DisposalViewModel>? _allDisposals; // Unfiltered list for filtering

    public TaxYearPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is (MainWindow mw, TaxYearSummary summary))
        {
            _mainWindow = mw;
            _summary = summary;

            // Show loading state immediately, then defer heavy work so the
            // ProgressRing has a chance to render before we block the UI thread.
            LoadingPanel.Visibility = Visibility.Visible;
            ContentScroller.Visibility = Visibility.Collapsed;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _isLoading = true;
                LoadData();
                _isLoading = false;

                LoadingPanel.Visibility = Visibility.Collapsed;
                ContentScroller.Visibility = Visibility.Visible;
            });
        }
    }

    private void LoadData()
    {
        if (_summary == null) return;

        TaxYearTitle.Text = $"Tax Year {_summary.TaxYear}";
        TaxYearDateRange.Text = $"6 April {_summary.StartYear} to 5 April {_summary.StartYear + 1}";

        // Load user inputs
        var userInput = _mainWindow?.Settings.TaxYearInputs.GetValueOrDefault(_summary.TaxYear);
        TaxableIncomeBox.Value = userInput?.TaxableIncome > 0 ? (double)userInput.TaxableIncome : double.NaN;
        OtherGainsBox.Value = userInput?.OtherCapitalGains > 0 ? (double)userInput.OtherCapitalGains : double.NaN;

        UpdateSummaryDisplay();
        LoadBalances();
        LoadDisposals();
        LoadWarnings();
        LoadStaking();
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

        CgtRatesLabel.Text = $"Basic rate: {_summary.BasicRateCgt:P0} / Higher rate: {_summary.HigherRateCgt:P0}";

        BasicRateText.Text = $"CGT basic rate: {_summary.BasicRateCgt:P0}";
        HigherRateText.Text = $"CGT higher/additional rate: {_summary.HigherRateCgt:P0}";
        BasicBandText.Text = $"Basic rate band: {FormatGbp(_summary.BasicRateBand)}";
        PersonalAllowanceText.Text = $"Personal allowance: {FormatGbp(_summary.PersonalAllowance)}";
    }

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

    private void LoadDisposals()
    {
        if (_summary == null) return;

        _allDisposals = _summary.Disposals
            .OrderBy(d => d.Date)
            .Select(d => new DisposalViewModel(d))
            .ToList();

        // Populate the asset filter dropdown with unique assets
        var assets = _allDisposals
            .Select(d => d.Asset)
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        DisposalAssetFilter.ItemsSource = assets;
        DisposalAssetFilter.SelectedIndex = -1;

        // Show all disposals initially
        DisposalsList.ItemsSource = _allDisposals;
        UpdateFilterStatus();
    }

    private void DisposalAssetFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDisposalFilter();
    }

    private void ClearDisposalFilter_Click(object sender, RoutedEventArgs e)
    {
        DisposalAssetFilter.SelectedIndex = -1;
        ApplyDisposalFilter();
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
        await _mainWindow.RecalculateAndBuildTabsAsync();

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
        await _mainWindow.RecalculateAndBuildTabsAsync();

        var updated = _mainWindow.TaxYearSummaries.FirstOrDefault(s => s.TaxYear == _summary.TaxYear);
        if (updated != null)
        {
            _summary = updated;
            UpdateSummaryDisplay();
        }
    }

    private void LoadWarnings()
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

        WarningsList.ItemsSource = _summary.Warnings
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

    private void LoadStaking()
    {
        if (_summary == null || _summary.StakingRewards.Count == 0)
        {
            StakingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StakingPanel.Visibility = Visibility.Visible;
        StakingTotalText.Text = $"Total staking/dividend income: {FormatGbp(_summary.StakingIncome)}";

        StakingList.ItemsSource = _summary.StakingRewards
            .OrderBy(s => s.Date)
            .Select(s => new StakingViewModel(s))
            .ToList();
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
        var tempCgtService = new CgtCalculationService(_mainWindow.FxService, tempWarnings, _mainWindow.Trades, _mainWindow.Settings.DelistedAssets);
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

    private static string FormatGbp(decimal amount)
    {
        return amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
    }
}

public class DisposalViewModel
{
    private readonly DisposalRecord _record;

    public DisposalViewModel(DisposalRecord record) => _record = record;

    public string DateFormatted => _record.Date.ToString("dd/MM/yyyy");
    public string Asset => _record.Asset;
    public string QuantityFormatted => _record.QuantityDisposed.ToString("0.########");
    public string ProceedsFormatted => FormatGbp(_record.DisposalProceeds);
    public string CostFormatted => FormatGbp(_record.AllowableCost);
    public string GainFormatted => FormatGbp(_record.GainOrLoss);
    public string MatchingRule => _record.MatchingRule;
    public SolidColorBrush GainColor => _record.GainOrLoss >= 0
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);

    private static string FormatGbp(decimal amount)
    {
        return amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
    }
}

public class WarningViewModel
{
    private readonly CalculationWarning _warning;

    public WarningViewModel(CalculationWarning warning) => _warning = warning;

    public string LevelText => _warning.Level.ToString().ToUpper();
    public string Category => _warning.Category;
    public string DateFormatted => _warning.DateFormatted;
    public string Message => _warning.Message;
    public SolidColorBrush LevelColor => _warning.Level switch
    {
        WarningLevel.Error => new SolidColorBrush(Colors.Red),
        WarningLevel.Warning => new SolidColorBrush(Colors.Orange),
        _ => new SolidColorBrush(Colors.Gray)
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
