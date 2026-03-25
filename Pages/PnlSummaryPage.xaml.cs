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

public sealed partial class PnlSummaryPage : Page
{
    private MainWindow? _mainWindow;
    private List<TaxYearSummary> _summaries = new();

    public PnlSummaryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is (MainWindow mw, List<TaxYearSummary> summaries))
        {
            _mainWindow = mw;
            _summaries = summaries.OrderBy(s => s.StartYear).ToList();
            BuildPnlCards();
        }
    }

    private void BuildPnlCards()
    {
        PnlContainer.Children.Clear();

        for (int i = 0; i < _summaries.Count; i++)
        {
            var summary = _summaries[i];
            var prev = i > 0 ? _summaries[i - 1] : null;

            var card = CreatePnlCard(summary, prev);
            PnlContainer.Children.Add(card);
        }
    }

    private Border CreatePnlCard(TaxYearSummary current, TaxYearSummary? previous)
    {
        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel { Spacing = 8 };

        // Title
        stack.Children.Add(new TextBlock
        {
            Text = $"Tax Year {current.TaxYear}",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"6 April {current.StartYear} to 5 April {current.StartYear + 1}",
            Opacity = 0.7
        });

        // P&L Grid
        var grid = new Grid
        {
            ColumnSpacing = 20,
            RowSpacing = 6,
            Margin = new Thickness(0, 8, 0, 0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int row = 0;

        // Header row
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 1, "Current Year", true, null, 0.6);
        if (previous != null)
            AddGridCell(grid, row, 2, "Previous Year", true, null, 0.6);
        row++;

        // Opening portfolio value
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Opening Portfolio Value", true);
        AddGridCell(grid, row, 1, FormatGbp(current.StartOfYearBalances.TotalGbpValue));
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.StartOfYearBalances.TotalGbpValue), false, null, 0.6);
        row++;

        // Disposal proceeds
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Total Disposal Proceeds");
        AddGridCell(grid, row, 1, FormatGbp(current.TotalDisposalProceeds));
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.TotalDisposalProceeds), false, null, 0.6);
        row++;

        // Allowable costs
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Total Allowable Costs");
        AddGridCell(grid, row, 1, FormatGbp(current.TotalAllowableCosts));
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.TotalAllowableCosts), false, null, 0.6);
        row++;

        // Divider
        grid.RowDefinitions.Add(new RowDefinition());
        var divider = new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 4, 0, 4)
        };
        Grid.SetRow(divider, row);
        Grid.SetColumnSpan(divider, 3);
        grid.Children.Add(divider);
        row++;

        // Total gains
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Total Gains");
        AddGridCell(grid, row, 1, FormatGbp(current.TotalGains), false, Colors.Green);
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.TotalGains), false, Colors.Green, 0.6);
        row++;

        // Total losses
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Total Losses");
        AddGridCell(grid, row, 1, FormatGbp(current.TotalLosses), false, Colors.Red);
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.TotalLosses), false, Colors.Red, 0.6);
        row++;

        // Net gain/loss
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Net Gain/Loss", true);
        AddGridCell(grid, row, 1, FormatGbp(current.NetGainOrLoss), true,
            current.NetGainOrLoss >= 0 ? Colors.Green : Colors.Red);
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.NetGainOrLoss), false,
                previous.NetGainOrLoss >= 0 ? Colors.Green : Colors.Red, 0.6);
        row++;

        // Staking income
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Staking / Dividend Income");
        AddGridCell(grid, row, 1, FormatGbp(current.StakingIncome));
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.StakingIncome), false, null, 0.6);
        row++;

        // CGT Due
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Capital Gains Tax Due", true);
        AddGridCell(grid, row, 1, FormatGbp(current.CgtDue), true, Colors.Red);
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.CgtDue), false, Colors.Red, 0.6);
        row++;

        // Divider
        grid.RowDefinitions.Add(new RowDefinition());
        var divider2 = new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 4, 0, 4)
        };
        Grid.SetRow(divider2, row);
        Grid.SetColumnSpan(divider2, 3);
        grid.Children.Add(divider2);
        row++;

        // Closing portfolio value
        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Closing Portfolio Value", true);
        AddGridCell(grid, row, 1, FormatGbp(current.EndOfYearBalances.TotalGbpValue), true);
        if (previous != null)
            AddGridCell(grid, row, 2, FormatGbp(previous.EndOfYearBalances.TotalGbpValue), false, null, 0.6);
        row++;

        // Year-on-year portfolio change
        var portfolioChange = current.EndOfYearBalances.TotalGbpValue - current.StartOfYearBalances.TotalGbpValue;
        var changePercent = current.StartOfYearBalances.TotalGbpValue != 0
            ? (portfolioChange / current.StartOfYearBalances.TotalGbpValue) * 100m : 0m;

        grid.RowDefinitions.Add(new RowDefinition());
        AddGridCell(grid, row, 0, "Portfolio Change (Year)");
        var changeText = $"{FormatGbp(portfolioChange)} ({changePercent:+0.0;-0.0;0.0}%)";
        AddGridCell(grid, row, 1, changeText, true, portfolioChange >= 0 ? Colors.Green : Colors.Red);
        row++;

        stack.Children.Add(grid);
        border.Child = stack;
        return border;
    }

    private void AddGridCell(Grid grid, int row, int col, string text, bool bold = false,
        Windows.UI.Color? color = null, double opacity = 1.0)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Opacity = opacity
        };
        if (color.HasValue)
            tb.Foreground = new SolidColorBrush(color.Value);

        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    // ========== Export handlers ==========

    private readonly ExportService _exportService = new();

    private async void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (_summaries.Count == 0) return;
        var path = await PickSaveFileAsync("Excel Workbook", ".xlsx", "PnL_Summary.xlsx");
        if (path == null) return;

        try
        {
            _exportService.ExportPnlSummaryToExcel(path, _summaries);
            ShowExportSuccess(path);
        }
        catch (Exception ex) { ShowExportError(ex); }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_summaries.Count == 0) return;
        var path = await PickSaveFileAsync("PDF Document", ".pdf", "PnL_Summary.pdf");
        if (path == null) return;

        try
        {
            _exportService.ExportPnlSummaryToPdf(path, _summaries);
            ShowExportSuccess(path);
        }
        catch (Exception ex) { ShowExportError(ex); }
    }

    private async System.Threading.Tasks.Task<string?> PickSaveFileAsync(string fileTypeLabel, string extension, string suggestedName)
    {
        var picker = new FileSavePicker();
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
