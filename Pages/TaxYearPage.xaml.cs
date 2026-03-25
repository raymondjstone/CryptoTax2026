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
            _isLoading = true;
            LoadData();
            _isLoading = false;
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

        var viewModels = _summary.Disposals
            .OrderBy(d => d.Date)
            .Select(d => new DisposalViewModel(d))
            .ToList();

        DisposalsList.ItemsSource = viewModels;
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
