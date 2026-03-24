using System;
using System.Collections.Generic;
using System.Linq;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

public class CgtCalculationService
{
    /// <summary>
    /// Calculates UK Capital Gains Tax for all tax years from trade history.
    /// Applies HMRC rules in order:
    /// 1. Same-day rule (match disposals with same-day acquisitions)
    /// 2. Bed & breakfast rule (match with acquisitions within 30 days AFTER disposal)
    /// 3. Section 104 pool (average cost basis for remaining)
    ///
    /// Trades in non-GBP quote currencies are treated as crypto-to-crypto disposals.
    /// </summary>
    public List<TaxYearSummary> CalculateAllTaxYears(
        List<KrakenTrade> trades,
        Dictionary<string, TaxYearUserInput> userInputs)
    {
        // Separate trades into acquisitions and disposals
        // For crypto bought with GBP: buy = acquisition of base asset
        // For crypto sold for GBP: sell = disposal of base asset
        // For crypto-to-crypto: this is a disposal of one and acquisition of another
        // Kraken quotes everything in the quote currency, so cost is in quote currency

        var gbpQuotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "GBP", "ZGBP" };

        // Build event list: each event is either an acquisition or disposal with GBP values
        var events = new List<TradeEvent>();

        foreach (var trade in trades.OrderBy(t => t.Time))
        {
            bool isGbpQuote = gbpQuotes.Contains(trade.QuoteAsset);

            if (isGbpQuote)
            {
                // Simple GBP pair
                if (trade.IsBuy)
                {
                    // Buying crypto with GBP = acquisition
                    events.Add(new TradeEvent
                    {
                        Date = trade.DateTime,
                        Asset = trade.BaseAsset,
                        IsAcquisition = true,
                        Quantity = trade.Volume,
                        GbpValue = trade.Cost + trade.Fee, // total cost including fees
                        TradeId = trade.TradeId
                    });
                }
                else
                {
                    // Selling crypto for GBP = disposal
                    events.Add(new TradeEvent
                    {
                        Date = trade.DateTime,
                        Asset = trade.BaseAsset,
                        IsAcquisition = false,
                        Quantity = trade.Volume,
                        GbpValue = trade.Cost - trade.Fee, // proceeds minus fees
                        TradeId = trade.TradeId
                    });
                }
            }
            else
            {
                // Crypto-to-crypto or crypto-to-USD/EUR etc.
                // For simplicity, we use the cost in the quote currency as a proxy.
                // In a production app you'd want historical GBP exchange rates.
                // For now we flag these and use the quoted cost as approximate GBP value.
                // TODO: Integrate historical FX rates for non-GBP pairs

                if (trade.IsBuy)
                {
                    // Buying base asset = acquisition of base, disposal of quote
                    events.Add(new TradeEvent
                    {
                        Date = trade.DateTime,
                        Asset = trade.BaseAsset,
                        IsAcquisition = true,
                        Quantity = trade.Volume,
                        GbpValue = trade.Cost + trade.Fee,
                        TradeId = trade.TradeId,
                        IsApproximate = true
                    });
                    events.Add(new TradeEvent
                    {
                        Date = trade.DateTime,
                        Asset = trade.QuoteAsset,
                        IsAcquisition = false,
                        Quantity = trade.Cost,
                        GbpValue = trade.Cost + trade.Fee,
                        TradeId = trade.TradeId,
                        IsApproximate = true
                    });
                }
                else
                {
                    // Selling base asset = disposal of base, acquisition of quote
                    events.Add(new TradeEvent
                    {
                        Date = trade.DateTime,
                        Asset = trade.BaseAsset,
                        IsAcquisition = false,
                        Quantity = trade.Volume,
                        GbpValue = trade.Cost - trade.Fee,
                        TradeId = trade.TradeId,
                        IsApproximate = true
                    });
                    events.Add(new TradeEvent
                    {
                        Date = trade.DateTime,
                        Asset = trade.QuoteAsset,
                        IsAcquisition = true,
                        Quantity = trade.Cost,
                        GbpValue = trade.Cost - trade.Fee,
                        TradeId = trade.TradeId,
                        IsApproximate = true
                    });
                }
            }
        }

        // Sort events by date
        events = events.OrderBy(e => e.Date).ToList();

        // Now apply matching rules to calculate gains
        var disposals = CalculateDisposals(events);

        // Group by tax year
        var taxYears = GetTaxYearsFromDisposals(disposals, userInputs);

        return taxYears;
    }

    private List<DisposalRecord> CalculateDisposals(List<TradeEvent> events)
    {
        var disposals = new List<DisposalRecord>();

        // Separate into acquisitions and disposals per asset
        var acquisitions = events.Where(e => e.IsAcquisition).ToList();
        var disposalEvents = events.Where(e => !e.IsAcquisition).ToList();

        // Track remaining quantity for each acquisition (for same-day and B&B matching)
        var remainingAcq = acquisitions.Select(a => new AcquisitionRemaining
        {
            Event = a,
            RemainingQuantity = a.Quantity
        }).ToList();

        // Section 104 pools per asset
        var pools = new Dictionary<string, Section104Pool>(StringComparer.OrdinalIgnoreCase);

        // First pass: identify same-day and B&B matches
        // We need to process disposals chronologically
        foreach (var disposal in disposalEvents.OrderBy(d => d.Date))
        {
            var asset = disposal.Asset;
            var remainingQty = disposal.Quantity;
            var disposalDate = disposal.Date.Date;

            // === RULE 1: Same-day matching ===
            var sameDayAcqs = remainingAcq
                .Where(a => a.Event.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)
                         && a.Event.Date.Date == disposalDate
                         && a.RemainingQuantity > 0)
                .ToList();

            foreach (var acq in sameDayAcqs)
            {
                if (remainingQty <= 0) break;

                var matchQty = Math.Min(remainingQty, acq.RemainingQuantity);
                var costProportion = acq.Event.Quantity > 0
                    ? (matchQty / acq.Event.Quantity) * acq.Event.GbpValue
                    : 0;
                var proceedsProportion = disposal.Quantity > 0
                    ? (matchQty / disposal.Quantity) * disposal.GbpValue
                    : 0;

                disposals.Add(new DisposalRecord
                {
                    Asset = asset,
                    Date = disposal.Date,
                    QuantityDisposed = matchQty,
                    DisposalProceeds = proceedsProportion,
                    AllowableCost = costProportion,
                    MatchingRule = "Same Day",
                    TradeId = disposal.TradeId,
                    TaxYear = GetTaxYearLabel(disposal.Date)
                });

                acq.RemainingQuantity -= matchQty;
                remainingQty -= matchQty;
            }

            // === RULE 2: Bed & Breakfast (30-day) rule ===
            // Match with acquisitions in the 30 days AFTER the disposal
            if (remainingQty > 0)
            {
                var bnbAcqs = remainingAcq
                    .Where(a => a.Event.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)
                             && a.Event.Date.Date > disposalDate
                             && a.Event.Date.Date <= disposalDate.AddDays(30)
                             && a.RemainingQuantity > 0)
                    .OrderBy(a => a.Event.Date)
                    .ToList();

                foreach (var acq in bnbAcqs)
                {
                    if (remainingQty <= 0) break;

                    var matchQty = Math.Min(remainingQty, acq.RemainingQuantity);
                    var costProportion = acq.Event.Quantity > 0
                        ? (matchQty / acq.Event.Quantity) * acq.Event.GbpValue
                        : 0;
                    var proceedsProportion = disposal.Quantity > 0
                        ? (matchQty / disposal.Quantity) * disposal.GbpValue
                        : 0;

                    disposals.Add(new DisposalRecord
                    {
                        Asset = asset,
                        Date = disposal.Date,
                        QuantityDisposed = matchQty,
                        DisposalProceeds = proceedsProportion,
                        AllowableCost = costProportion,
                        MatchingRule = "Bed & Breakfast",
                        TradeId = disposal.TradeId,
                        TaxYear = GetTaxYearLabel(disposal.Date)
                    });

                    acq.RemainingQuantity -= matchQty;
                    remainingQty -= matchQty;
                }
            }

            // Acquisitions not consumed by same-day/B&B go into Section 104 pool
            // We need to add all acquisitions up to and on this date that haven't been added yet
            // Actually, we handle pool building separately below
        }

        // === Build Section 104 pools and match remaining disposals ===
        // Reset and process chronologically, adding acquisitions to pool and
        // removing from pool on disposal (after same-day/B&B already handled above)

        // Track which acquisitions were consumed by same-day/B&B
        var consumedAcqQuantities = new Dictionary<TradeEvent, decimal>();
        foreach (var ra in remainingAcq)
        {
            var consumed = ra.Event.Quantity - ra.RemainingQuantity;
            if (consumed > 0)
                consumedAcqQuantities[ra.Event] = consumed;
        }

        // Track disposal quantities already matched
        var matchedDisposalQty = disposals
            .GroupBy(d => d.TradeId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.QuantityDisposed));

        // Process all events chronologically for Section 104
        foreach (var evt in events.OrderBy(e => e.Date).ThenBy(e => e.IsAcquisition ? 0 : 1))
        {
            var asset = evt.Asset;
            if (!pools.ContainsKey(asset))
                pools[asset] = new Section104Pool { Asset = asset };

            var pool = pools[asset];

            if (evt.IsAcquisition)
            {
                // Add to pool only the portion not consumed by same-day/B&B
                var consumed = consumedAcqQuantities.GetValueOrDefault(evt, 0m);
                var poolQty = evt.Quantity - consumed;
                if (poolQty > 0)
                {
                    var poolCost = evt.Quantity > 0
                        ? (poolQty / evt.Quantity) * evt.GbpValue
                        : 0;
                    pool.AddTokens(poolQty, poolCost);
                }
            }
            else
            {
                // Disposal: match remaining quantity from Section 104 pool
                var alreadyMatched = matchedDisposalQty.GetValueOrDefault(evt.TradeId, 0m);
                var poolQty = evt.Quantity - alreadyMatched;

                if (poolQty > 0 && pool.Quantity > 0)
                {
                    var actualQty = Math.Min(poolQty, pool.Quantity);
                    var costFromPool = pool.RemoveTokens(actualQty);
                    var proceedsProportion = evt.Quantity > 0
                        ? (actualQty / evt.Quantity) * evt.GbpValue
                        : 0;

                    disposals.Add(new DisposalRecord
                    {
                        Asset = asset,
                        Date = evt.Date,
                        QuantityDisposed = actualQty,
                        DisposalProceeds = proceedsProportion,
                        AllowableCost = costFromPool,
                        MatchingRule = "Section 104",
                        TradeId = evt.TradeId,
                        TaxYear = GetTaxYearLabel(evt.Date)
                    });
                }
            }
        }

        return disposals.OrderBy(d => d.Date).ToList();
    }

    private List<TaxYearSummary> GetTaxYearsFromDisposals(
        List<DisposalRecord> disposals,
        Dictionary<string, TaxYearUserInput> userInputs)
    {
        var grouped = disposals.GroupBy(d => d.TaxYear);
        var summaries = new List<TaxYearSummary>();

        foreach (var group in grouped.OrderBy(g => g.Key))
        {
            var taxYearLabel = group.Key;
            var startYear = int.Parse(taxYearLabel.Split('/')[0]);
            var rates = UkTaxRates.GetRatesForYear(startYear);

            var userInput = userInputs.GetValueOrDefault(taxYearLabel, new TaxYearUserInput());

            var disposalList = group.ToList();
            var totalProceeds = disposalList.Sum(d => d.DisposalProceeds);
            var totalCosts = disposalList.Sum(d => d.AllowableCost);
            var totalGains = disposalList.Where(d => d.GainOrLoss > 0).Sum(d => d.GainOrLoss);
            var totalLosses = disposalList.Where(d => d.GainOrLoss < 0).Sum(d => d.GainOrLoss);
            var netGain = totalGains + totalLosses;

            // Apply annual exempt amount
            var otherGains = userInput.OtherCapitalGains;
            var totalNetGain = netGain + otherGains;
            var taxableGain = Math.Max(0, totalNetGain - rates.AnnualExemptAmount);

            // Calculate CGT due based on income tax band
            var taxableIncome = userInput.TaxableIncome;
            var cgtDue = CalculateCgt(taxableGain, taxableIncome, rates);

            summaries.Add(new TaxYearSummary
            {
                TaxYear = taxYearLabel,
                StartYear = startYear,
                TaxableIncome = taxableIncome,
                OtherCapitalGains = otherGains,
                Disposals = disposalList,
                TotalDisposalProceeds = totalProceeds,
                TotalAllowableCosts = totalCosts,
                TotalGains = totalGains,
                TotalLosses = totalLosses,
                AnnualExemptAmount = rates.AnnualExemptAmount,
                TaxableGain = taxableGain,
                CgtDue = cgtDue,
                BasicRateCgt = rates.BasicRateCgt,
                HigherRateCgt = rates.HigherRateCgt,
                BasicRateBand = rates.BasicRateBand,
                PersonalAllowance = rates.PersonalAllowance
            });
        }

        return summaries;
    }

    private decimal CalculateCgt(decimal taxableGain, decimal taxableIncome, UkTaxRates rates)
    {
        if (taxableGain <= 0) return 0;

        // Determine how much of the basic rate band is unused by income
        var incomeAbovePA = Math.Max(0, taxableIncome - rates.PersonalAllowance);
        var unusedBasicBand = Math.Max(0, rates.BasicRateBand - incomeAbovePA);

        // Gains within unused basic band taxed at basic CGT rate
        var gainsAtBasicRate = Math.Min(taxableGain, unusedBasicBand);
        var gainsAtHigherRate = taxableGain - gainsAtBasicRate;

        return (gainsAtBasicRate * rates.BasicRateCgt) + (gainsAtHigherRate * rates.HigherRateCgt);
    }

    public static string GetTaxYearLabel(DateTimeOffset date)
    {
        // UK tax year runs 6 April to 5 April
        int year = date.Year;
        if (date.Month < 4 || (date.Month == 4 && date.Day <= 5))
            year--; // Falls in previous tax year

        int endYear = (year + 1) % 100;
        return $"{year}/{endYear:D2}";
    }

    private class TradeEvent
    {
        public DateTimeOffset Date { get; set; }
        public string Asset { get; set; } = "";
        public bool IsAcquisition { get; set; }
        public decimal Quantity { get; set; }
        public decimal GbpValue { get; set; }
        public string TradeId { get; set; } = "";
        public bool IsApproximate { get; set; }
    }

    private class AcquisitionRemaining
    {
        public TradeEvent Event { get; set; } = null!;
        public decimal RemainingQuantity { get; set; }
    }
}
