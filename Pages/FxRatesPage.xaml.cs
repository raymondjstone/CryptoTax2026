using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using CryptoTax2026.Services;

namespace CryptoTax2026.Pages;

public sealed partial class FxRatesPage : Page
{
    private MainWindow? _mainWindow;
    private FxConversionService? _fxService;

    public FxRatesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is (MainWindow mw, FxConversionService fx))
        {
            _mainWindow = mw;
            _fxService = fx;
            LoadData();
        }
    }

    private void LoadData()
    {
        if (_fxService == null) return;

        CachePathText.Text = $"Cache folder: {_fxService.CacheFolderPath}";

        var stats = _fxService.GetCacheStats();
        var totalPoints = stats.Sum(s => s.DataPoints);
        var onDiskCount = stats.Count(s => s.OnDisk);

        SummaryText.Text = $"{stats.Count} pairs cached, {totalPoints:#,##0} total data points, {onDiskCount} persisted to disk";

        var viewModels = stats.Select(s => new FxRateRow(s)).ToList();
        RatesListView.ItemsSource = viewModels;
    }
}

internal class FxRateRow
{
    private readonly FxCacheInfo _info;

    public FxRateRow(FxCacheInfo info) => _info = info;

    public string PairName => _info.PairName;
    public string DataPointsFormatted => _info.DataPoints.ToString("#,##0");
    public string EarliestFormatted => _info.EarliestDate.ToString("dd/MM/yyyy HH:mm");
    public string LatestFormatted => _info.LatestDate.ToString("dd/MM/yyyy HH:mm");
    public string RateFormatted => _info.SampleRate.ToString("0.########");
    public string OnDiskLabel => _info.OnDisk ? "Yes" : "No";
    public string DataSource => _info.DataSource;
}
