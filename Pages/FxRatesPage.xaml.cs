using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
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

        LoadManualOverrides();
    }

    private void LoadManualOverrides()
    {
        if (_fxService == null) return;

        ManualOverridesPanel.Children.Clear();

        var overrides = _fxService.GetManualOverrides();
        if (overrides.Count == 0)
        {
            ManualOverridesPanel.Children.Add(new TextBlock { Text = "No manual rates entered.", Opacity = 0.6 });
            return;
        }

        // Header
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void AddHeader(int col, string text)
        {
            var tb = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            Grid.SetColumn(tb, col);
            header.Children.Add(tb);
        }
        AddHeader(0, "Asset"); AddHeader(1, "Date"); AddHeader(2, "GBP Rate");
        ManualOverridesPanel.Children.Add(header);

        foreach (var (asset, date, rate) in overrides)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var assetText = new TextBlock { Text = asset, VerticalAlignment = VerticalAlignment.Center };
            var dateText  = new TextBlock { Text = date.ToString("dd/MM/yyyy"), VerticalAlignment = VerticalAlignment.Center };
            var rateText  = new TextBlock { Text = rate.ToString("0.##########"), VerticalAlignment = VerticalAlignment.Center };
            var removeBtn = new Button { Content = "Remove", Tag = (asset, date) };
            removeBtn.Click += RemoveOverride_Click;

            Grid.SetColumn(assetText, 0); Grid.SetColumn(dateText, 1);
            Grid.SetColumn(rateText, 2);  Grid.SetColumn(removeBtn, 3);
            row.Children.Add(assetText); row.Children.Add(dateText);
            row.Children.Add(rateText);  row.Children.Add(removeBtn);
            ManualOverridesPanel.Children.Add(row);
        }
    }

    private void AddOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_fxService == null) return;

        var asset = ManualAssetBox.Text.Trim().ToUpperInvariant();
        var rateText = ManualRateBox.Text.Trim();

        if (string.IsNullOrEmpty(asset))
        {
            OverrideInfoBar.Message = "Please enter an asset name.";
            OverrideInfoBar.Severity = InfoBarSeverity.Warning;
            OverrideInfoBar.IsOpen = true;
            return;
        }

        if (!decimal.TryParse(rateText, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var rate) || rate < 0)
        {
            OverrideInfoBar.Message = "Please enter a valid non-negative number for the GBP rate.";
            OverrideInfoBar.Severity = InfoBarSeverity.Warning;
            OverrideInfoBar.IsOpen = true;
            return;
        }

        // Use the selected date, defaulting to today if nothing chosen
        var selectedDate = ManualDatePicker.SelectedDate.HasValue
            ? new DateTimeOffset(ManualDatePicker.SelectedDate.Value.DateTime, TimeSpan.Zero)
            : DateTimeOffset.UtcNow.Date == default ? DateTimeOffset.UtcNow
              : new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);

        _fxService.SetManualOverride(asset, selectedDate, rate);
        ManualAssetBox.Text = "";
        ManualRateBox.Text = "";

        OverrideInfoBar.Message = $"Rate saved: 1 {asset} = {rate:0.##########} GBP on {selectedDate:dd/MM/yyyy}.";
        OverrideInfoBar.Severity = InfoBarSeverity.Success;
        OverrideInfoBar.IsOpen = true;

        LoadManualOverrides();
    }

    private void RemoveOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_fxService == null || sender is not Button btn) return;
        if (btn.Tag is not (string asset, DateTimeOffset date)) return;

        _fxService.RemoveManualOverride(asset, date);

        OverrideInfoBar.Message = $"Rate for {asset} on {date:dd/MM/yyyy} removed.";
        OverrideInfoBar.Severity = InfoBarSeverity.Informational;
        OverrideInfoBar.IsOpen = true;

        LoadManualOverrides();
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
