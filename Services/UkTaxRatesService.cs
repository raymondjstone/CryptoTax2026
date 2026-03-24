using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

public class UkTaxRatesService
{
    private readonly HttpClient _httpClient;

    public UkTaxRatesService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "CryptoTax2026/1.0");
    }

    /// <summary>
    /// Attempts to fetch current CGT rates from gov.uk.
    /// Falls back to hardcoded rates if the fetch fails.
    /// </summary>
    public async Task<UkTaxRates> GetCurrentRatesAsync()
    {
        try
        {
            // Try to scrape the CGT rates page
            var html = await _httpClient.GetStringAsync(
                "https://www.gov.uk/capital-gains-tax/rates");

            var rates = ParseGovUkRates(html);
            if (rates != null)
                return rates;
        }
        catch
        {
            // Fall back to hardcoded rates
        }

        // Return latest known rates (2025/26)
        return UkTaxRates.GetRatesForYear(2025);
    }

    /// <summary>
    /// Attempts to fetch the Annual Exempt Amount from gov.uk.
    /// </summary>
    public async Task<decimal?> GetAnnualExemptAmountAsync()
    {
        try
        {
            var html = await _httpClient.GetStringAsync(
                "https://www.gov.uk/capital-gains-tax/allowances");

            // Look for the AEA figure
            var match = Regex.Match(html, @"£([\d,]+)\s*(?:tax-free allowance|annual exempt)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var amount = decimal.Parse(match.Groups[1].Value.Replace(",", ""));
                return amount;
            }
        }
        catch
        {
            // Fall back
        }

        return null;
    }

    private UkTaxRates? ParseGovUkRates(string html)
    {
        // Try to extract CGT rates from the page content
        // Gov.uk pages have structured content we can parse

        // Look for patterns like "10%" and "20%" or "18%" and "24%" for CGT rates
        var basicMatch = Regex.Match(html, @"basic[\s\-]*rate[\s\S]{0,100}?(\d+)%", RegexOptions.IgnoreCase);
        var higherMatch = Regex.Match(html, @"higher[\s\S]{0,100}?(\d+)%", RegexOptions.IgnoreCase);

        if (basicMatch.Success && higherMatch.Success)
        {
            var basicRate = decimal.Parse(basicMatch.Groups[1].Value) / 100m;
            var higherRate = decimal.Parse(higherMatch.Groups[1].Value) / 100m;

            // Only accept if rates are in a reasonable range for CGT
            if (basicRate is >= 0.05m and <= 0.30m && higherRate is >= 0.10m and <= 0.45m)
            {
                return new UkTaxRates
                {
                    TaxYearStart = DateTime.Now.Month >= 4 && DateTime.Now.Day >= 6
                        ? DateTime.Now.Year
                        : DateTime.Now.Year - 1,
                    BasicRateCgt = basicRate,
                    HigherRateCgt = higherRate,
                    AnnualExemptAmount = 3000m, // Will be updated separately
                    BasicRateBand = 37700m,
                    PersonalAllowance = 12570m
                };
            }
        }

        return null;
    }
}
