using CryptoTax2026.Models;
using Xunit;

namespace CryptoTax2026.Tests.Models;

public class UkTaxRatesTests
{
    [Theory]
    [InlineData(2020)]
    [InlineData(2021)]
    [InlineData(2022)]
    public void PreApr2023_HasCorrectRates(int year)
    {
        var rates = UkTaxRates.GetRatesForYear(year);
        Assert.Equal(12300m, rates.AnnualExemptAmount);
        Assert.Equal(0.10m, rates.BasicRateCgt);
        Assert.Equal(0.20m, rates.HigherRateCgt);
    }

    [Fact]
    public void TaxYear2023_HasReducedAea()
    {
        var rates = UkTaxRates.GetRatesForYear(2023);
        Assert.Equal(6000m, rates.AnnualExemptAmount);
        Assert.Equal(0.10m, rates.BasicRateCgt);
        Assert.Equal(0.20m, rates.HigherRateCgt);
    }

    [Fact]
    public void TaxYear2024_HasFurtherReducedAea()
    {
        var rates = UkTaxRates.GetRatesForYear(2024);
        Assert.Equal(3000m, rates.AnnualExemptAmount);
        Assert.Equal(0.10m, rates.BasicRateCgt);
        Assert.Equal(0.20m, rates.HigherRateCgt);
    }

    [Fact]
    public void TaxYear2025_HasIncreasedCgtRates()
    {
        var rates = UkTaxRates.GetRatesForYear(2025);
        Assert.Equal(3000m, rates.AnnualExemptAmount);
        Assert.Equal(0.18m, rates.BasicRateCgt);
        Assert.Equal(0.24m, rates.HigherRateCgt);
    }

    [Fact]
    public void FutureYear_UsesLatestKnownRates()
    {
        var rates = UkTaxRates.GetRatesForYear(2030);
        Assert.Equal(3000m, rates.AnnualExemptAmount);
        Assert.Equal(0.18m, rates.BasicRateCgt);
        Assert.Equal(0.24m, rates.HigherRateCgt);
    }

    [Fact]
    public void AllYears_HaveConsistentBandAndAllowance()
    {
        foreach (var year in new[] { 2020, 2023, 2024, 2025, 2030 })
        {
            var rates = UkTaxRates.GetRatesForYear(year);
            Assert.Equal(37700m, rates.BasicRateBand);
            Assert.Equal(12570m, rates.PersonalAllowance);
        }
    }
}
