namespace CryptoTax2026.Models;

public class UkTaxRates
{
    public int TaxYearStart { get; set; } // e.g. 2023 for 2023/24
    public decimal AnnualExemptAmount { get; set; }
    public decimal BasicRateCgt { get; set; }    // CGT rate for basic rate taxpayers
    public decimal HigherRateCgt { get; set; }   // CGT rate for higher/additional rate taxpayers
    public decimal BasicRateBand { get; set; }   // Basic rate income tax band upper limit
    public decimal PersonalAllowance { get; set; }

    public static UkTaxRates GetRatesForYear(int startYear)
    {
        // UK CGT rates for crypto (classified as "other gains" not residential property)
        // These changed significantly over recent years
        return startYear switch
        {
            // Pre-2024 rates: 10%/20%, AEA £12,300
            <= 2022 => new UkTaxRates
            {
                TaxYearStart = startYear,
                AnnualExemptAmount = 12300m,
                BasicRateCgt = 0.10m,
                HigherRateCgt = 0.20m,
                BasicRateBand = 37700m,
                PersonalAllowance = 12570m
            },
            // 2023/24: AEA halved to £6,000, rates still 10%/20%
            2023 => new UkTaxRates
            {
                TaxYearStart = 2023,
                AnnualExemptAmount = 6000m,
                BasicRateCgt = 0.10m,
                HigherRateCgt = 0.20m,
                BasicRateBand = 37700m,
                PersonalAllowance = 12570m
            },
            // 2024/25: AEA reduced to £3,000, rates still 10%/20%
            2024 => new UkTaxRates
            {
                TaxYearStart = 2024,
                AnnualExemptAmount = 3000m,
                BasicRateCgt = 0.10m,
                HigherRateCgt = 0.20m,
                BasicRateBand = 37700m,
                PersonalAllowance = 12570m
            },
            // 2025/26: AEA £3,000, rates increased to 18%/24% from Oct 2024 Budget
            2025 => new UkTaxRates
            {
                TaxYearStart = 2025,
                AnnualExemptAmount = 3000m,
                BasicRateCgt = 0.18m,
                HigherRateCgt = 0.24m,
                BasicRateBand = 37700m,
                PersonalAllowance = 12570m
            },
            // Future years: use latest known rates
            _ => new UkTaxRates
            {
                TaxYearStart = startYear,
                AnnualExemptAmount = 3000m,
                BasicRateCgt = 0.18m,
                HigherRateCgt = 0.24m,
                BasicRateBand = 37700m,
                PersonalAllowance = 12570m
            }
        };
    }
}
