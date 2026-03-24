using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WordDoc = DocumentFormat.OpenXml.Wordprocessing;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

public class ExportService
{
    static ExportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    // ========== EXCEL ==========

    public void ExportToExcel(string filePath, TaxYearSummary summary, List<KrakenTrade>? krakenTrades = null)
    {
        using var workbook = new XLWorkbook();

        // Summary sheet
        var ws = workbook.Worksheets.Add("CGT Summary");
        WriteSummarySheet(ws, summary);

        // Disposals sheet
        var ds = workbook.Worksheets.Add("Disposals");
        WriteDisposalsSheet(ds, summary);

        // Staking income sheet
        if (summary.StakingRewards.Count > 0)
        {
            var ss = workbook.Worksheets.Add("Staking Income");
            WriteStakingSheet(ss, summary);
        }

        // Warnings sheet
        if (summary.Warnings.Count > 0)
        {
            var wws = workbook.Worksheets.Add("Warnings");
            WriteWarningsSheet(wws, summary);
        }

        // Kraken trades sheet
        if (krakenTrades != null && krakenTrades.Count > 0)
        {
            var ks = workbook.Worksheets.Add("Kraken Trades");
            WriteKrakenTradesSheet(ks, krakenTrades, summary);
        }

        workbook.SaveAs(filePath);
    }

    public void ExportAllYearsToExcel(string filePath, List<TaxYearSummary> summaries, List<KrakenTrade>? krakenTrades = null)
    {
        using var workbook = new XLWorkbook();

        foreach (var summary in summaries.OrderBy(s => s.StartYear))
        {
            var ws = workbook.Worksheets.Add($"{summary.TaxYear} Summary");
            WriteSummarySheet(ws, summary);

            var ds = workbook.Worksheets.Add($"{summary.TaxYear} Disposals");
            WriteDisposalsSheet(ds, summary);

            if (summary.StakingRewards.Count > 0)
            {
                var ss = workbook.Worksheets.Add($"{summary.TaxYear} Staking");
                WriteStakingSheet(ss, summary);
            }

            if (summary.Warnings.Count > 0)
            {
                var wws = workbook.Worksheets.Add($"{summary.TaxYear} Warnings");
                WriteWarningsSheet(wws, summary);
            }
        }

        if (krakenTrades != null && krakenTrades.Count > 0)
        {
            var ks = workbook.Worksheets.Add("All Kraken Trades");
            WriteKrakenTradesSheet(ks, krakenTrades, null);
        }

        workbook.SaveAs(filePath);
    }

    private void WriteSummarySheet(IXLWorksheet ws, TaxYearSummary summary)
    {
        ws.Cell("A1").Value = $"UK Capital Gains Tax - Tax Year {summary.TaxYear}";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;

        ws.Cell("A2").Value = $"6 April {summary.StartYear} to 5 April {summary.StartYear + 1}";
        ws.Cell("A2").Style.Font.Italic = true;

        int row = 4;
        void AddRow(string label, decimal value, string format = "£#,##0.00")
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = value;
            ws.Cell(row, 2).Style.NumberFormat.Format = format;
            row++;
        }

        ws.Cell(row, 1).Value = "YOUR DETAILS";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 11;
        row++;
        AddRow("Taxable Income", summary.TaxableIncome);
        AddRow("Other Capital Gains", summary.OtherCapitalGains);
        row++;

        ws.Cell(row, 1).Value = "CAPITAL GAINS SUMMARY";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 11;
        row++;
        AddRow("Number of Disposals", summary.Disposals.Count, "#,##0");
        AddRow("Total Disposal Proceeds", summary.TotalDisposalProceeds);
        AddRow("Total Allowable Costs", summary.TotalAllowableCosts);
        AddRow("Total Gains", summary.TotalGains);
        AddRow("Total Losses", summary.TotalLosses);
        AddRow("Net Gain/Loss", summary.NetGainOrLoss);
        row++;

        ws.Cell(row, 1).Value = "TAX CALCULATION";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 11;
        row++;
        AddRow("Annual Exempt Amount", summary.AnnualExemptAmount);
        AddRow("Taxable Gain", summary.TaxableGain);
        AddRow("CGT Basic Rate", summary.BasicRateCgt, "0.0%");
        AddRow("CGT Higher Rate", summary.HigherRateCgt, "0.0%");
        AddRow("Basic Rate Band", summary.BasicRateBand);
        AddRow("Personal Allowance", summary.PersonalAllowance);
        row++;

        ws.Cell(row, 1).Value = "CAPITAL GAINS TAX DUE";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 12;
        ws.Cell(row, 2).Value = summary.CgtDue;
        ws.Cell(row, 2).Style.NumberFormat.Format = "£#,##0.00";
        ws.Cell(row, 2).Style.Font.Bold = true;
        ws.Cell(row, 2).Style.Font.FontSize = 12;
        row += 2;

        if (summary.StakingRewards.Count > 0)
        {
            ws.Cell(row, 1).Value = "STAKING / DIVIDEND INCOME";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 11;
            row++;
            AddRow("Total Staking Income", summary.StakingIncome);
            ws.Cell(row, 1).Value = "(Taxed as miscellaneous income, separate from CGT)";
            ws.Cell(row, 1).Style.Font.Italic = true;
            row++;
        }

        if (summary.Warnings.Count > 0)
        {
            row++;
            ws.Cell(row, 1).Value = $"DATA ISSUES ({summary.Warnings.Count})";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 11;
            ws.Cell(row, 1).Style.Font.FontColor = XLColor.OrangeRed;
            row++;
            ws.Cell(row, 1).Value = "See Warnings sheet for details";
            ws.Cell(row, 1).Style.Font.Italic = true;
        }

        ws.Columns().AdjustToContents();
    }

    private void WriteDisposalsSheet(IXLWorksheet ws, TaxYearSummary summary)
    {
        var headers = new[] { "Date", "Asset", "Quantity", "Proceeds (£)", "Cost (£)", "Gain/Loss (£)", "Matching Rule", "Trade ID" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
            ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        int row = 2;
        foreach (var d in summary.Disposals.OrderBy(d => d.Date))
        {
            ws.Cell(row, 1).Value = d.Date.ToString("dd/MM/yyyy");
            ws.Cell(row, 2).Value = d.Asset;
            ws.Cell(row, 3).Value = d.QuantityDisposed;
            ws.Cell(row, 3).Style.NumberFormat.Format = "0.########";
            ws.Cell(row, 4).Value = d.DisposalProceeds;
            ws.Cell(row, 4).Style.NumberFormat.Format = "£#,##0.00";
            ws.Cell(row, 5).Value = d.AllowableCost;
            ws.Cell(row, 5).Style.NumberFormat.Format = "£#,##0.00";
            ws.Cell(row, 6).Value = d.GainOrLoss;
            ws.Cell(row, 6).Style.NumberFormat.Format = "£#,##0.00";
            if (d.GainOrLoss < 0)
                ws.Cell(row, 6).Style.Font.FontColor = XLColor.Red;
            ws.Cell(row, 7).Value = d.MatchingRule;
            ws.Cell(row, 8).Value = d.TradeId;
            row++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private void WriteKrakenTradesSheet(IXLWorksheet ws, List<KrakenTrade> trades, TaxYearSummary? filterYear)
    {
        var filtered = filterYear != null
            ? trades.Where(t =>
            {
                var label = CgtCalculationService.GetTaxYearLabel(t.DateTime);
                return label == filterYear.TaxYear;
            }).ToList()
            : trades;

        var headers = new[] { "Date/Time", "Trade ID", "Pair", "Base Asset", "Quote Asset", "Type", "Price", "Volume", "Cost", "Fee", "Order Type" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
            ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        int row = 2;
        foreach (var t in filtered.OrderBy(t => t.Time))
        {
            ws.Cell(row, 1).Value = t.DateTime.ToString("dd/MM/yyyy HH:mm:ss");
            ws.Cell(row, 2).Value = t.TradeId;
            ws.Cell(row, 3).Value = t.Pair;
            ws.Cell(row, 4).Value = t.BaseAsset;
            ws.Cell(row, 5).Value = t.QuoteAsset;
            ws.Cell(row, 6).Value = t.Type;
            ws.Cell(row, 7).Value = t.Price;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.########";
            ws.Cell(row, 8).Value = t.Volume;
            ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.########";
            ws.Cell(row, 9).Value = t.Cost;
            ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 10).Value = t.Fee;
            ws.Cell(row, 10).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 11).Value = t.OrderType;
            row++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private void WriteStakingSheet(IXLWorksheet ws, TaxYearSummary summary)
    {
        ws.Cell("A1").Value = $"Staking / Dividend Income - Tax Year {summary.TaxYear}";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = "Taxed as miscellaneous income, separate from Capital Gains Tax";
        ws.Cell("A2").Style.Font.Italic = true;
        ws.Cell("A3").Value = $"Total Staking Income: {FormatGbp(summary.StakingIncome)}";
        ws.Cell("A3").Style.Font.Bold = true;

        var headers = new[] { "Date", "Asset", "Amount", "GBP Value" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(5, i + 1).Value = headers[i];
            ws.Cell(5, i + 1).Style.Font.Bold = true;
            ws.Cell(5, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        int row = 6;
        foreach (var s in summary.StakingRewards.OrderBy(s => s.Date))
        {
            ws.Cell(row, 1).Value = s.Date.ToString("dd/MM/yyyy");
            ws.Cell(row, 2).Value = s.Asset;
            ws.Cell(row, 3).Value = s.Amount;
            ws.Cell(row, 3).Style.NumberFormat.Format = "0.########";
            ws.Cell(row, 4).Value = s.GbpValue;
            ws.Cell(row, 4).Style.NumberFormat.Format = "£#,##0.00";
            row++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(5);
    }

    private void WriteWarningsSheet(IXLWorksheet ws, TaxYearSummary summary)
    {
        ws.Cell("A1").Value = $"Data Issues / Warnings - Tax Year {summary.TaxYear}";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = "These issues were found during calculation and may affect accuracy.";
        ws.Cell("A2").Style.Font.Italic = true;

        var headers = new[] { "Level", "Category", "Date", "Asset", "Message" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(4, i + 1).Value = headers[i];
            ws.Cell(4, i + 1).Style.Font.Bold = true;
            ws.Cell(4, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        int row = 5;
        foreach (var w in summary.Warnings.OrderByDescending(w => w.Level).ThenBy(w => w.Date))
        {
            ws.Cell(row, 1).Value = w.Level.ToString().ToUpper();
            ws.Cell(row, 1).Style.Font.FontColor = w.Level switch
            {
                WarningLevel.Error => XLColor.Red,
                WarningLevel.Warning => XLColor.OrangeRed,
                _ => XLColor.Gray
            };
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = w.Category;
            ws.Cell(row, 3).Value = w.DateFormatted;
            ws.Cell(row, 4).Value = w.Asset ?? "";
            ws.Cell(row, 5).Value = w.Message;
            row++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(4);
    }

    // ========== PDF ==========

    public void ExportToPdf(string filePath, TaxYearSummary summary, List<KrakenTrade>? krakenTrades = null)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text($"UK Capital Gains Tax Report - Tax Year {summary.TaxYear}")
                        .FontSize(16).Bold();
                    col.Item().Text($"6 April {summary.StartYear} to 5 April {summary.StartYear + 1}")
                        .FontSize(10).Italic();
                    col.Item().PaddingBottom(10).LineHorizontal(1);
                });

                page.Content().Column(col =>
                {
                    // Summary section
                    col.Item().Text("Summary").FontSize(12).Bold();
                    col.Item().PaddingTop(5).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3);
                            c.RelativeColumn(2);
                        });

                        AddPdfRow(table, "Taxable Income", FormatGbp(summary.TaxableIncome));
                        AddPdfRow(table, "Other Capital Gains", FormatGbp(summary.OtherCapitalGains));
                        AddPdfRow(table, "Number of Disposals", summary.Disposals.Count.ToString());
                        AddPdfRow(table, "Total Disposal Proceeds", FormatGbp(summary.TotalDisposalProceeds));
                        AddPdfRow(table, "Total Allowable Costs", FormatGbp(summary.TotalAllowableCosts));
                        AddPdfRow(table, "Total Gains", FormatGbp(summary.TotalGains));
                        AddPdfRow(table, "Total Losses", FormatGbp(summary.TotalLosses));
                        AddPdfRow(table, "Net Gain/Loss", FormatGbp(summary.NetGainOrLoss));
                        AddPdfRow(table, "Annual Exempt Amount", FormatGbp(summary.AnnualExemptAmount));
                        AddPdfRow(table, "Taxable Gain", FormatGbp(summary.TaxableGain));
                        AddPdfRow(table, $"CGT Rates: {summary.BasicRateCgt:P0} / {summary.HigherRateCgt:P0}", "");
                        AddPdfRow(table, "CAPITAL GAINS TAX DUE", FormatGbp(summary.CgtDue), true);
                    });

                    // Disposals table
                    col.Item().PaddingTop(15).Text("Disposals").FontSize(12).Bold();
                    col.Item().PaddingTop(5).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(2);  // Date
                            c.RelativeColumn(1.2f);  // Asset
                            c.RelativeColumn(2);  // Qty
                            c.RelativeColumn(2);  // Proceeds
                            c.RelativeColumn(2);  // Cost
                            c.RelativeColumn(2);  // Gain
                            c.RelativeColumn(2);  // Rule
                        });

                        // Header
                        table.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Date").Bold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Asset").Bold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Quantity").Bold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Proceeds").Bold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Cost").Bold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Gain/Loss").Bold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Rule").Bold();
                        });

                        foreach (var d in summary.Disposals.OrderBy(d => d.Date))
                        {
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                .Text(d.Date.ToString("dd/MM/yyyy"));
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                .Text(d.Asset);
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                .Text(d.QuantityDisposed.ToString("0.########"));
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                .Text(FormatGbp(d.DisposalProceeds));
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                .Text(FormatGbp(d.AllowableCost));
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                .Text(FormatGbp(d.GainOrLoss))
                                .FontColor(d.GainOrLoss < 0 ? Colors.Red.Medium : Colors.Black);
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                .Text(d.MatchingRule);
                        }
                    });

                    // Staking income
                    if (summary.StakingRewards.Count > 0)
                    {
                        col.Item().PaddingTop(15).Text("Staking / Dividend Income").FontSize(12).Bold();
                        col.Item().Text("Taxed as miscellaneous income, separate from CGT.").FontSize(8).Italic();
                        col.Item().Text($"Total: {FormatGbp(summary.StakingIncome)}").Bold();
                        col.Item().PaddingTop(5).Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(2);
                                c.RelativeColumn(1.2f);
                                c.RelativeColumn(2);
                                c.RelativeColumn(2);
                            });

                            table.Header(h =>
                            {
                                h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Date").Bold();
                                h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Asset").Bold();
                                h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Amount").Bold();
                                h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("GBP Value").Bold();
                            });

                            foreach (var s in summary.StakingRewards.OrderBy(s => s.Date))
                            {
                                table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                    .Text(s.Date.ToString("dd/MM/yyyy"));
                                table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                    .Text(s.Asset);
                                table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                    .Text(s.Amount.ToString("0.########"));
                                table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                    .Text(FormatGbp(s.GbpValue));
                            }
                        });
                    }

                    // Warnings
                    if (summary.Warnings.Count > 0)
                    {
                        col.Item().PaddingTop(15).Text($"Data Issues ({summary.Warnings.Count})").FontSize(12).Bold()
                            .FontColor(Colors.Orange.Darken2);
                        col.Item().PaddingTop(5).Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(1);
                                c.RelativeColumn(1.2f);
                                c.RelativeColumn(1.5f);
                                c.RelativeColumn(5);
                            });

                            table.Header(h =>
                            {
                                h.Cell().Background(Colors.Orange.Lighten4).Padding(3).Text("Level").Bold();
                                h.Cell().Background(Colors.Orange.Lighten4).Padding(3).Text("Category").Bold();
                                h.Cell().Background(Colors.Orange.Lighten4).Padding(3).Text("Date").Bold();
                                h.Cell().Background(Colors.Orange.Lighten4).Padding(3).Text("Message").Bold();
                            });

                            foreach (var w in summary.Warnings.OrderByDescending(w => w.Level).ThenBy(w => w.Date))
                            {
                                var levelColor = w.Level switch
                                {
                                    WarningLevel.Error => Colors.Red.Medium,
                                    WarningLevel.Warning => Colors.Orange.Darken1,
                                    _ => Colors.Grey.Darken1
                                };
                                table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                    .Text(w.Level.ToString().ToUpper()).FontColor(levelColor).Bold();
                                table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                    .Text(w.Category);
                                table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                    .Text(w.DateFormatted);
                                table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                    .Text(w.Message);
                            }
                        });
                    }

                    // Kraken trades
                    if (krakenTrades != null && krakenTrades.Count > 0)
                    {
                        var yearTrades = krakenTrades
                            .Where(t => CgtCalculationService.GetTaxYearLabel(t.DateTime) == summary.TaxYear)
                            .OrderBy(t => t.Time)
                            .ToList();

                        if (yearTrades.Count > 0)
                        {
                            col.Item().PageBreak();
                            col.Item().Text("Kraken Trade History").FontSize(12).Bold();
                            col.Item().PaddingTop(5).Table(table =>
                            {
                                table.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn(2.5f); // DateTime
                                    c.RelativeColumn(1);    // Type
                                    c.RelativeColumn(1.5f); // Pair
                                    c.RelativeColumn(2);    // Volume
                                    c.RelativeColumn(1.5f); // Price
                                    c.RelativeColumn(1.5f); // Cost
                                    c.RelativeColumn(1);    // Fee
                                });

                                table.Header(h =>
                                {
                                    h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Date/Time").Bold();
                                    h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Type").Bold();
                                    h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Pair").Bold();
                                    h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Volume").Bold();
                                    h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Price").Bold();
                                    h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Cost").Bold();
                                    h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Fee").Bold();
                                });

                                foreach (var t in yearTrades)
                                {
                                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                        .Text(t.DateTime.ToString("dd/MM/yyyy HH:mm"));
                                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                        .Text(t.Type.ToUpper());
                                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                        .Text($"{t.BaseAsset}/{t.QuoteAsset}");
                                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                        .Text(t.Volume.ToString("0.########"));
                                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                        .Text(t.Price.ToString("#,##0.##"));
                                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                        .Text(t.Cost.ToString("#,##0.00"));
                                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                                        .Text(t.Fee.ToString("#,##0.00"));
                                }
                            });
                        }
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Generated by CryptoTax2026 on ");
                    t.Span(DateTime.Now.ToString("dd MMM yyyy HH:mm"));
                    t.Span(" | Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        });

        document.GeneratePdf(filePath);
    }

    private void AddPdfRow(TableDescriptor table, string label, string value, bool bold = false)
    {
        if (bold)
        {
            table.Cell().Padding(2).Text(label).Bold();
            table.Cell().Padding(2).AlignRight().Text(value).Bold();
        }
        else
        {
            table.Cell().Padding(2).Text(label);
            table.Cell().Padding(2).AlignRight().Text(value);
        }
    }

    // ========== WORD ==========

    public void ExportToWord(string filePath, TaxYearSummary summary, List<KrakenTrade>? krakenTrades = null)
    {
        using var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new WordDoc.Document();
        var body = mainPart.Document.AppendChild(new WordDoc.Body());

        body.AppendChild(CreateWordParagraph(
            $"UK Capital Gains Tax Report - Tax Year {summary.TaxYear}", true, "28"));
        body.AppendChild(CreateWordParagraph(
            $"6 April {summary.StartYear} to 5 April {summary.StartYear + 1}", false, "20", true));
        body.AppendChild(new WordDoc.Paragraph());

        body.AppendChild(CreateWordParagraph("Summary", true, "24"));
        var summaryTable = CreateWordTable(new[] { "Item", "Value" }, new[]
        {
            new[] { "Taxable Income", FormatGbp(summary.TaxableIncome) },
            new[] { "Other Capital Gains", FormatGbp(summary.OtherCapitalGains) },
            new[] { "Number of Disposals", summary.Disposals.Count.ToString() },
            new[] { "Total Disposal Proceeds", FormatGbp(summary.TotalDisposalProceeds) },
            new[] { "Total Allowable Costs", FormatGbp(summary.TotalAllowableCosts) },
            new[] { "Total Gains", FormatGbp(summary.TotalGains) },
            new[] { "Total Losses", FormatGbp(summary.TotalLosses) },
            new[] { "Net Gain/Loss", FormatGbp(summary.NetGainOrLoss) },
            new[] { "Annual Exempt Amount", FormatGbp(summary.AnnualExemptAmount) },
            new[] { "Taxable Gain", FormatGbp(summary.TaxableGain) },
            new[] { "CGT Rates", $"{summary.BasicRateCgt:P0} / {summary.HigherRateCgt:P0}" },
            new[] { "CAPITAL GAINS TAX DUE", FormatGbp(summary.CgtDue) },
        });
        body.AppendChild(summaryTable);
        body.AppendChild(new WordDoc.Paragraph());

        body.AppendChild(CreateWordParagraph("Disposals", true, "24"));
        var disposalHeaders = new[] { "Date", "Asset", "Qty", "Proceeds", "Cost", "Gain/Loss", "Rule" };
        var disposalRows = summary.Disposals.OrderBy(d => d.Date).Select(d => new[]
        {
            d.Date.ToString("dd/MM/yyyy"),
            d.Asset,
            d.QuantityDisposed.ToString("0.####"),
            FormatGbp(d.DisposalProceeds),
            FormatGbp(d.AllowableCost),
            FormatGbp(d.GainOrLoss),
            d.MatchingRule
        }).ToArray();
        body.AppendChild(CreateWordTable(disposalHeaders, disposalRows));

        // Staking income
        if (summary.StakingRewards.Count > 0)
        {
            body.AppendChild(new WordDoc.Paragraph());
            body.AppendChild(CreateWordParagraph("Staking / Dividend Income", true, "24"));
            body.AppendChild(CreateWordParagraph(
                "Taxed as miscellaneous income, separate from Capital Gains Tax.", false, "18", true));
            body.AppendChild(CreateWordParagraph(
                $"Total Staking Income: {FormatGbp(summary.StakingIncome)}", true, "20"));

            var stakingHeaders = new[] { "Date", "Asset", "Amount", "GBP Value" };
            var stakingRows = summary.StakingRewards.OrderBy(s => s.Date).Select(s => new[]
            {
                s.Date.ToString("dd/MM/yyyy"),
                s.Asset,
                s.Amount.ToString("0.########"),
                FormatGbp(s.GbpValue)
            }).ToArray();
            body.AppendChild(CreateWordTable(stakingHeaders, stakingRows));
        }

        // Warnings
        if (summary.Warnings.Count > 0)
        {
            body.AppendChild(new WordDoc.Paragraph());
            body.AppendChild(CreateWordParagraph($"Data Issues ({summary.Warnings.Count})", true, "24"));
            body.AppendChild(CreateWordParagraph(
                "These issues were found during calculation and may affect the accuracy of results.", false, "18", true));

            var warningHeaders = new[] { "Level", "Category", "Date", "Message" };
            var warningRows = summary.Warnings
                .OrderByDescending(w => w.Level).ThenBy(w => w.Date)
                .Select(w => new[]
                {
                    w.Level.ToString().ToUpper(),
                    w.Category,
                    w.DateFormatted,
                    w.Message
                }).ToArray();
            body.AppendChild(CreateWordTable(warningHeaders, warningRows));
        }

        if (krakenTrades != null && krakenTrades.Count > 0)
        {
            var yearTrades = krakenTrades
                .Where(t => CgtCalculationService.GetTaxYearLabel(t.DateTime) == summary.TaxYear)
                .OrderBy(t => t.Time)
                .ToList();

            if (yearTrades.Count > 0)
            {
                body.AppendChild(new WordDoc.Paragraph());
                body.AppendChild(CreateWordParagraph("Kraken Trade History", true, "24"));
                var tradeHeaders = new[] { "Date/Time", "Type", "Pair", "Volume", "Price", "Cost", "Fee" };
                var tradeRows = yearTrades.Select(t => new[]
                {
                    t.DateTime.ToString("dd/MM/yyyy HH:mm"),
                    t.Type.ToUpper(),
                    $"{t.BaseAsset}/{t.QuoteAsset}",
                    t.Volume.ToString("0.########"),
                    t.Price.ToString("#,##0.##"),
                    t.Cost.ToString("#,##0.00"),
                    t.Fee.ToString("#,##0.00")
                }).ToArray();
                body.AppendChild(CreateWordTable(tradeHeaders, tradeRows));
            }
        }

        body.AppendChild(new WordDoc.Paragraph());
        body.AppendChild(CreateWordParagraph(
            $"Generated by CryptoTax2026 on {DateTime.Now:dd MMM yyyy HH:mm}", false, "16", true));
    }

    private WordDoc.Paragraph CreateWordParagraph(string text, bool bold, string fontSize, bool italic = false)
    {
        var run = new WordDoc.Run();
        var runProps = new WordDoc.RunProperties();
        if (bold) runProps.AppendChild(new WordDoc.Bold());
        if (italic) runProps.AppendChild(new WordDoc.Italic());
        runProps.AppendChild(new WordDoc.FontSize { Val = fontSize });
        run.AppendChild(runProps);
        run.AppendChild(new WordDoc.Text(text));
        return new WordDoc.Paragraph(run);
    }

    private WordDoc.Table CreateWordTable(string[] headers, string[][] rows)
    {
        var table = new WordDoc.Table();

        var tblProps = new WordDoc.TableProperties(
            new WordDoc.TableBorders(
                new WordDoc.TopBorder { Val = WordDoc.BorderValues.Single, Size = 4 },
                new WordDoc.BottomBorder { Val = WordDoc.BorderValues.Single, Size = 4 },
                new WordDoc.LeftBorder { Val = WordDoc.BorderValues.Single, Size = 4 },
                new WordDoc.RightBorder { Val = WordDoc.BorderValues.Single, Size = 4 },
                new WordDoc.InsideHorizontalBorder { Val = WordDoc.BorderValues.Single, Size = 2 },
                new WordDoc.InsideVerticalBorder { Val = WordDoc.BorderValues.Single, Size = 2 }
            ),
            new WordDoc.TableWidth { Width = "5000", Type = WordDoc.TableWidthUnitValues.Pct }
        );
        table.AppendChild(tblProps);

        var headerRow = new WordDoc.TableRow();
        foreach (var h in headers)
        {
            var cell = new WordDoc.TableCell();
            cell.AppendChild(new WordDoc.TableCellProperties(
                new WordDoc.Shading { Val = WordDoc.ShadingPatternValues.Clear, Fill = "D9D9D9" }));
            var run = new WordDoc.Run(
                new WordDoc.RunProperties(new WordDoc.Bold(), new WordDoc.FontSize { Val = "18" }),
                new WordDoc.Text(h));
            cell.AppendChild(new WordDoc.Paragraph(run));
            headerRow.AppendChild(cell);
        }
        table.AppendChild(headerRow);

        foreach (var row in rows)
        {
            var tableRow = new WordDoc.TableRow();
            foreach (var cellValue in row)
            {
                var cell = new WordDoc.TableCell();
                var run = new WordDoc.Run(
                    new WordDoc.RunProperties(new WordDoc.FontSize { Val = "16" }),
                    new WordDoc.Text(cellValue ?? ""));
                cell.AppendChild(new WordDoc.Paragraph(run));
                tableRow.AppendChild(cell);
            }
            table.AppendChild(tableRow);
        }

        return table;
    }

    private static string FormatGbp(decimal amount)
    {
        return amount < 0 ? $"-\u00a3{Math.Abs(amount):#,##0.00}" : $"\u00a3{amount:#,##0.00}";
    }
}
