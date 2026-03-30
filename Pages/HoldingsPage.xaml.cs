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

public sealed partial class HoldingsPage : Page
{
    private MainWindow? _mainWindow;
    private bool _holdingsExpanded = true;

    public HoldingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainWindow mw)
        {
            _mainWindow = mw;
            LoadPools();
        }
    }

    private void LoadPools()
    {
        if (_mainWindow == null) return;

        var fxService = _mainWindow.FxService;
        var now = DateTimeOffset.UtcNow;

        var pools = _mainWindow.FinalPools
            .Where(p => p.Value.Quantity > 0.00000001m)
            .OrderByDescending(p => p.Value.PooledCost)
            .Select(p =>
            {
                decimal estValue = 0;
                if (fxService != null)
                {
                    try { estValue = fxService.GetGbpValueOfAsset(p.Value.Asset, p.Value.Quantity, now); }
                    catch { /* rate not available */ }
                }
                return new PoolViewModel(p.Value, estValue);
            })
            .ToList();

        PoolsList.ItemsSource = pools;

        var totalCost = pools.Sum(p => p.Pool.PooledCost);
        var totalValue = pools.Sum(p => p.EstValue);
        var totalGain = totalValue - totalCost;
        var gainSign = totalGain >= 0 ? "+" : "";
        UnrealisedSummaryText.Text = $"Total cost: £{totalCost:#,##0.00}  |  Est. value: £{totalValue:#,##0.00}  |  " +
            $"Unrealised: {gainSign}£{totalGain:#,##0.00}  (using latest cached FX rates)";
    }

    private void HoldingsCollapse_Click(object sender, RoutedEventArgs e)
    {
        _holdingsExpanded = !_holdingsExpanded;
        HoldingsContent.Visibility = _holdingsExpanded ? Visibility.Visible : Visibility.Collapsed;
        HoldingsCollapseBtn.Content = _holdingsExpanded ? "▲" : "▼";
    }
}

public class PoolViewModel
{
    public Section104Pool Pool { get; }
    public decimal EstValue { get; }

    public PoolViewModel(Section104Pool pool, decimal estValue = 0)
    {
        Pool = pool;
        EstValue = estValue;
        History = pool.History
            .OrderBy(h => h.Date)
            .Select(h => new PoolHistoryViewModel(h))
            .ToList();
    }

    public string Asset => Pool.Asset;
    public string QuantityFormatted => Pool.Quantity.ToString("0.########");
    public string PooledCostFormatted => FormatGbp(Pool.PooledCost);
    public string EstValueFormatted => EstValue > 0 ? FormatGbp(EstValue) : "N/A";
    public string UnrealisedGainFormatted => EstValue > 0 ? FormatGbp(EstValue - Pool.PooledCost) : "";
    public string GainPercentFormatted
    {
        get
        {
            if (EstValue <= 0 || Pool.PooledCost == 0) return "";
            var pct = (EstValue - Pool.PooledCost) / Pool.PooledCost * 100m;
            return $"{pct:+0.0;-0.0;0.0}%";
        }
    }
    public string AvgCostFormatted => Pool.Quantity > 0
        ? $"£{Pool.CostPerUnit:#,##0.00######}"
        : "N/A";
    public string AcquisitionCount => Pool.History.Count.ToString();
    public SolidColorBrush GainColor => EstValue - Pool.PooledCost >= 0
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);

    public List<PoolHistoryViewModel> History { get; }

    private static string FormatGbp(decimal amount)
    {
        return amount < 0 ? $"-£{Math.Abs(amount):#,##0.00}" : $"£{amount:#,##0.00}";
    }
}

public class PoolHistoryViewModel
{
    private readonly PoolHistoryEntry _entry;
    public PoolHistoryViewModel(PoolHistoryEntry entry) => _entry = entry;

    public string DateFormatted => _entry.Date.ToString("dd/MM/yyyy");
    public string TypeFormatted => string.IsNullOrEmpty(_entry.Type) ? "trade" : _entry.Type;
    public string QuantityFormatted => _entry.Quantity.ToString("0.########");
    public string CostFormatted => $"£{_entry.Cost:#,##0.00}";
    public string CostPerUnitFormatted => _entry.Quantity > 0
        ? $"£{(_entry.Cost / _entry.Quantity):#,##0.00######}"
        : "";
    public string RefId => _entry.RefId;
}

