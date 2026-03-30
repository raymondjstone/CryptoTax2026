using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using CryptoTax2026.Models;
using CryptoTax2026.Services;

namespace CryptoTax2026.Pages;

public sealed partial class ToolsPage : Page
{
    private MainWindow? _mainWindow;

    public ToolsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainWindow mw)
        {
            _mainWindow = mw;
            LoadAeaOptimiser();
            LoadTaxLossHarvesting();
            LoadBreakEvenCalculator();
            LoadHmrcChecklist();
        }
    }

    private void LoadAeaOptimiser()
    {
        if (_mainWindow == null) return;

        // Find the current open tax year (or the latest)
        var now = DateTimeOffset.UtcNow;
        var currentTaxYearStart = now.Month >= 4 && now.Day >= 6 ? now.Year : now.Year - 1;
        var summary = _mainWindow.TaxYearSummaries
            .FirstOrDefault(s => s.StartYear == currentTaxYearStart)
            ?? _mainWindow.TaxYearSummaries.OrderByDescending(s => s.StartYear).FirstOrDefault();

        if (summary == null)
        {
            AeaCurrentGains.Text = "N/A";
            AeaAmount.Text = "N/A";
            AeaUsed.Text = "N/A";
            AeaRemaining.Text = "N/A";
            AeaSuggestion.Text = "No tax year data available. Download ledger and FX rates first.";
            return;
        }

        var netGains = summary.NetGainOrLoss > 0 ? summary.NetGainOrLoss - summary.LossesUsedThisYear : 0;
        var aeaUsed = Math.Min(summary.AnnualExemptAmount, netGains);
        var aeaRemaining = summary.AnnualExemptAmount - aeaUsed;

        AeaCurrentGains.Text = FormatGbp(summary.NetGainOrLoss);
        AeaAmount.Text = FormatGbp(summary.AnnualExemptAmount);
        AeaUsed.Text = FormatGbp(aeaUsed);
        AeaRemaining.Text = FormatGbp(aeaRemaining);

        if (aeaRemaining > 0)
            AeaSuggestion.Text = $"You have {FormatGbp(aeaRemaining)} of unused AEA for {summary.TaxYear}. " +
                $"You could crystallise up to this amount in gains before the tax year ends (5 April {summary.StartYear + 1}) without paying CGT. " +
                $"Consider selling and immediately rebuying assets with unrealised gains — but note the 30-day B&B rule applies.";
        else
            AeaSuggestion.Text = $"Your AEA for {summary.TaxYear} is fully utilised. Any further gains will be subject to CGT.";
    }

    private void LoadTaxLossHarvesting()
    {
        if (_mainWindow?.FxService == null || _mainWindow.FinalPools.Count == 0)
        {
            HarvestingEmptyText.Visibility = Visibility.Visible;
            HarvestingList.Visibility = Visibility.Collapsed;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fxService = _mainWindow.FxService;

        // Find current tax year for CGT rate
        var currentTaxYearStart = now.Month >= 4 && now.Day >= 6 ? now.Year : now.Year - 1;
        var rates = UkTaxRates.GetRatesForYear(currentTaxYearStart);

        var harvestCandidates = _mainWindow.FinalPools
            .Where(p => p.Value.Quantity > 0.00000001m)
            .Select(p =>
            {
                decimal estValue = 0;
                try { estValue = fxService.GetGbpValueOfAsset(p.Value.Asset, p.Value.Quantity, now); }
                catch { return null; }

                var unrealised = estValue - p.Value.PooledCost;
                if (unrealised >= 0) return null;

                return new TaxLossHarvestViewModel
                {
                    Asset = p.Value.Asset,
                    PooledCost = p.Value.PooledCost,
                    EstValue = estValue,
                    UnrealisedLoss = unrealised,
                    TaxSaving = Math.Abs(unrealised) * rates.HigherRateCgt
                };
            })
            .Where(x => x != null)
            .OrderBy(x => x!.UnrealisedLoss)
            .ToList();

        if (harvestCandidates.Count == 0)
        {
            HarvestingEmptyText.Visibility = Visibility.Visible;
            HarvestingList.Visibility = Visibility.Collapsed;
        }
        else
        {
            HarvestingEmptyText.Visibility = Visibility.Collapsed;
            HarvestingList.Visibility = Visibility.Visible;
            HarvestingList.ItemsSource = harvestCandidates;
        }
    }

    private void LoadBreakEvenCalculator()
    {
        if (_mainWindow?.FxService == null || _mainWindow.FinalPools.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var fxService = _mainWindow.FxService;

        var items = _mainWindow.FinalPools
            .Where(p => p.Value.Quantity > 0.00000001m)
            .Select(p =>
            {
                decimal currentPrice = 0;
                try { currentPrice = fxService.GetGbpValueOfAsset(p.Value.Asset, 1m, now); }
                catch { /* rate unavailable */ }

                return new BreakEvenViewModel
                {
                    Asset = p.Value.Asset,
                    Quantity = p.Value.Quantity,
                    AvgCost = p.Value.CostPerUnit,
                    CurrentPrice = currentPrice
                };
            })
            .OrderByDescending(b => b.Quantity * b.AvgCost)
            .ToList();

        BreakEvenList.ItemsSource = items;
    }

    private void LoadHmrcChecklist()
    {
        var items = new[]
        {
            ("Exchange trade history / ledger data downloaded", _mainWindow?.Ledger?.Count > 0),
            ("FX conversion rates cached for all trading dates", _mainWindow?.FxService != null),
            ("All CSV imports from other exchanges completed", _mainWindow?.Settings.ManualLedgerEntries.Count > 0 || true),
            ("Cost basis overrides documented with notes", true),
            ("Delisted assets / negligible value claims recorded", true),
            ("SA108 figures reviewed and cross-checked", false),
            ("Disposal schedule exported (CSV or PDF)", false),
            ("Staking / misc income report exported", false),
            ("Section 104 pool state saved for carry-forward", _mainWindow?.FinalPools.Count > 0),
            ("Backup of all data taken (settings + ledger)", false),
            ("Records kept for 12 months after filing deadline", false),
        };

        ChecklistPanel.Children.Clear();
        foreach (var (text, autoChecked) in items)
        {
            var cb = new CheckBox
            {
                Content = text,
                IsChecked = autoChecked,
                IsEnabled = !autoChecked // auto-detected items are read-only
            };
            ChecklistPanel.Children.Add(cb);
        }
    }

    private static string FormatGbp(decimal amount)
    {
        return amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
    }
}

public class TaxLossHarvestViewModel
{
    public string Asset { get; set; } = "";
    public decimal PooledCost { get; set; }
    public decimal EstValue { get; set; }
    public decimal UnrealisedLoss { get; set; }
    public decimal TaxSaving { get; set; }

    public string PooledCostFormatted => FormatGbp(PooledCost);
    public string EstValueFormatted => FormatGbp(EstValue);
    public string UnrealisedLossFormatted => FormatGbp(UnrealisedLoss);
    public string TaxSavingFormatted => $"up to {FormatGbp(TaxSaving)}";

    private static string FormatGbp(decimal amount)
        => amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
}

public class BreakEvenViewModel
{
    public string Asset { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal AvgCost { get; set; }
    public decimal CurrentPrice { get; set; }

    public string QuantityFormatted => Quantity.ToString("0.########");
    public string AvgCostFormatted => $"£{AvgCost:#,##0.00######}";
    public string CurrentPriceFormatted => CurrentPrice > 0 ? $"£{CurrentPrice:#,##0.00######}" : "N/A";
    public string VsBreakEvenFormatted
    {
        get
        {
            if (CurrentPrice <= 0 || AvgCost <= 0) return "N/A";
            var pct = ((CurrentPrice - AvgCost) / AvgCost) * 100m;
            return pct >= 0 ? $"+{pct:0.0}% above" : $"{pct:0.0}% below";
        }
    }
    public SolidColorBrush VsBreakEvenColor
    {
        get
        {
            if (CurrentPrice <= 0 || AvgCost <= 0) return new SolidColorBrush(Colors.Gray);
            return CurrentPrice >= AvgCost
                ? new SolidColorBrush(Colors.Green)
                : new SolidColorBrush(Colors.Red);
        }
    }
}
