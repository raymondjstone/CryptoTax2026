using System;
using System.Collections.Generic;
using System.Linq;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

public class CgtCalculationService
{
    private readonly FxConversionService _fxService;
    private readonly List<CalculationWarning> _warnings;

    public CgtCalculationService(FxConversionService fxService, List<CalculationWarning> warnings)
    {
        _fxService = fxService;
        _warnings = warnings;
    }

    /// <summary>
    /// Calculates UK Capital Gains Tax for all tax years from ledger entries.
    /// Uses HMRC matching rules: same-day, bed &amp; breakfast (30-day), Section 104 pool.
    ///
    /// The ledger gives us exact amounts per asset per event, grouped by refid for trades.
    /// Staking rewards are tracked as miscellaneous income, not capital gains.
    /// </summary>
    // Ledger types that represent internal balance moves, NOT taxable events.
    // Transfers between spot and staking wallets are just repositioning — not disposals.
    private static readonly HashSet<string> NonTaxableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer",   // spot↔staking, spot↔futures, internal moves
        "margin",     // margin position adjustments (not a disposal)
        "rollover",   // futures rollover
        "settled",    // settled margin position
        "reserve",    // reserves held by Kraken
        "conversion", // Kraken internal conversions (e.g., ETH2→ETH unstaking)
        "creator_fee" // NFT creator fees
    };

    // Ledger subtypes that are staking-related internal moves (not taxable)
    private static readonly HashSet<string> StakingTransferSubtypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotfromstaking", "stakingfromspot", "spottostaking", "stakingtospot",
        "spotfromfutures", "futuresfromspot", "spottofutures", "futurestospot"
    };

    public List<TaxYearSummary> CalculateAllTaxYears(
        List<KrakenLedgerEntry> ledger,
        Dictionary<string, TaxYearUserInput> userInputs)
    {
        // Build trade events from ledger entries
        var events = BuildEventsFromLedger(ledger);

        // Separate out staking/reward income (staking, dividend, reward types with positive amount)
        var stakingEntries = ledger
            .Where(e => e.Type is "staking" or "dividend" or "reward" && e.Amount > 0)
            .ToList();

        // Calculate disposals using HMRC rules
        var disposals = CalculateDisposals(events);

        // Compute balance snapshots at tax year boundaries
        var snapshots = ComputeBalanceSnapshots(ledger);

        // Group by tax year and build summaries
        return BuildTaxYearSummaries(disposals, stakingEntries, userInputs, snapshots);
    }

    private List<CgtEvent> BuildEventsFromLedger(List<KrakenLedgerEntry> ledger)
    {
        var events = new List<CgtEvent>();

        // Group ledger entries by refid — a trade will have 2+ entries with the same refid
        // (e.g., -0.5 ETH and +500 GBP for selling ETH)
        // Exclude non-taxable types (transfers, margin, rollover etc.)
        var tradeEntries = ledger
            .Where(e => e.Type == "trade")
            .Where(e => !StakingTransferSubtypes.Contains(e.SubType))
            .GroupBy(e => e.RefId)
            .ToList();

        foreach (var group in tradeEntries)
        {
            var entries = group.OrderBy(e => e.Time).ToList();
            if (entries.Count < 2)
            {
                _warnings.Add(new CalculationWarning
                {
                    Level = WarningLevel.Warning,
                    Category = "Ledger",
                    Message = $"Trade refid {group.Key} has only {entries.Count} ledger entry (expected 2+). Skipping.",
                    Date = entries.FirstOrDefault()?.DateTime,
                    LedgerId = entries.FirstOrDefault()?.LedgerId
                });
                continue;
            }

            // Detect flex staking / internal transfers disguised as "trades":
            // Both sides normalise to the same asset (e.g. ETH → XETH.F or SOL → SOL.F).
            // These are just moving coins in/out of staking, NOT real trades.
            var distinctAssets = entries
                .Select(e => e.NormalisedAsset)
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctAssets.Count == 1)
            {
                // All entries normalise to the same asset — this is a staking transfer, skip it
                continue;
            }

            // Separate the entries into "received" (positive amount) and "spent" (negative amount)
            var received = entries.Where(e => e.Amount > 0).ToList();
            var spent = entries.Where(e => e.Amount < 0).ToList();

            // Also account for fee entries (amount=0 but fee>0, or fee on the main entries)
            if (received.Count == 0 || spent.Count == 0)
            {
                // Edge case: fee-only entries or margin adjustments
                _warnings.Add(new CalculationWarning
                {
                    Level = WarningLevel.Info,
                    Category = "Ledger",
                    Message = $"Trade refid {group.Key}: no clear buy/sell sides. Entries: {string.Join(", ", entries.Select(e => $"{e.NormalisedAsset} {e.Amount}"))}",
                    Date = entries.First().DateTime,
                    LedgerId = entries.First().LedgerId
                });
                continue;
            }

            var date = entries.First().DateTime;

            // For each spent asset: it's a disposal
            // For each received asset: it's an acquisition
            // We need GBP values for both sides

            // Figure out the GBP value of this trade
            // Best case: one side is GBP and we know the exact value
            // Otherwise: convert using FX rates

            decimal tradeGbpValue = 0;
            bool hasDirectGbp = false;

            // Check if any side is GBP
            var gbpEntry = entries.FirstOrDefault(e => e.NormalisedAsset == "GBP");
            if (gbpEntry != null)
            {
                tradeGbpValue = Math.Abs(gbpEntry.Amount) - gbpEntry.Fee;
                hasDirectGbp = true;
            }

            // Check if any side is a fiat currency we can convert
            if (!hasDirectGbp)
            {
                var fiatEntry = entries.FirstOrDefault(e => e.IsFiat);
                if (fiatEntry != null)
                {
                    var fiatAmount = Math.Abs(fiatEntry.Amount) - fiatEntry.Fee;
                    tradeGbpValue = _fxService.ConvertToGbp(fiatAmount, fiatEntry.NormalisedAsset, date);
                    hasDirectGbp = true; // We have a fiat-derived GBP value
                }
            }

            // Check if any side is a stablecoin
            if (!hasDirectGbp)
            {
                var stableEntry = entries.FirstOrDefault(e =>
                    e.NormalisedAsset is "USDT" or "USDC" or "DAI");
                if (stableEntry != null)
                {
                    var stableAmount = Math.Abs(stableEntry.Amount) - stableEntry.Fee;
                    tradeGbpValue = _fxService.ConvertToGbp(stableAmount, stableEntry.NormalisedAsset, date);
                    hasDirectGbp = true;
                }
            }

            // If purely crypto-to-crypto with no fiat side, value using the disposal side
            if (!hasDirectGbp)
            {
                // Use the spent (disposal) side's GBP value
                foreach (var s in spent)
                {
                    tradeGbpValue += _fxService.ConvertToGbp(
                        Math.Abs(s.Amount), s.NormalisedAsset, date);
                }
            }

            // Now create events for each non-fiat, non-stablecoin asset involved
            foreach (var s in spent)
            {
                if (s.IsFiat || s.NormalisedAsset is "USDT" or "USDC" or "DAI") continue;

                var qty = Math.Abs(s.Amount);
                var fee = s.Fee;

                // Proportional GBP value if multiple crypto assets were spent
                var totalSpentCrypto = spent.Where(x => !x.IsFiat && x.NormalisedAsset is not "USDT" and not "USDC" and not "DAI")
                    .Sum(x => Math.Abs(x.Amount));
                var proportion = totalSpentCrypto > 0 ? qty / totalSpentCrypto : 1m;

                events.Add(new CgtEvent
                {
                    Date = date,
                    Asset = s.NormalisedAsset,
                    IsAcquisition = false,
                    Quantity = qty,
                    Fee = fee,
                    GbpValue = tradeGbpValue * proportion,
                    RefId = group.Key,
                    LedgerId = s.LedgerId
                });
            }

            foreach (var r in received)
            {
                if (r.IsFiat || r.NormalisedAsset is "USDT" or "USDC" or "DAI") continue;

                var qty = r.Amount;
                var fee = r.Fee;

                var totalReceivedCrypto = received.Where(x => !x.IsFiat && x.NormalisedAsset is not "USDT" and not "USDC" and not "DAI")
                    .Sum(x => x.Amount);
                var proportion = totalReceivedCrypto > 0 ? qty / totalReceivedCrypto : 1m;

                events.Add(new CgtEvent
                {
                    Date = date,
                    Asset = r.NormalisedAsset,
                    IsAcquisition = true,
                    Quantity = qty,
                    Fee = fee,
                    GbpValue = (tradeGbpValue * proportion) + _fxService.ConvertToGbp(fee, r.NormalisedAsset, date),
                    RefId = group.Key,
                    LedgerId = r.LedgerId
                });
            }

            // Handle stablecoin acquisitions/disposals as well (they are crypto, not fiat)
            foreach (var e in entries.Where(e => e.NormalisedAsset is "USDT" or "USDC" or "DAI"))
            {
                if (e.Amount > 0)
                {
                    events.Add(new CgtEvent
                    {
                        Date = date,
                        Asset = e.NormalisedAsset,
                        IsAcquisition = true,
                        Quantity = e.Amount,
                        Fee = e.Fee,
                        GbpValue = _fxService.ConvertToGbp(e.Amount, e.NormalisedAsset, date),
                        RefId = group.Key,
                        LedgerId = e.LedgerId
                    });
                }
                else if (e.Amount < 0)
                {
                    events.Add(new CgtEvent
                    {
                        Date = date,
                        Asset = e.NormalisedAsset,
                        IsAcquisition = false,
                        Quantity = Math.Abs(e.Amount),
                        Fee = e.Fee,
                        GbpValue = _fxService.ConvertToGbp(Math.Abs(e.Amount), e.NormalisedAsset, date),
                        RefId = group.Key,
                        LedgerId = e.LedgerId
                    });
                }
            }
        }

        // Deposits of crypto = acquisition at GBP value on date received
        // (we don't know the original cost, so we use market value — user should adjust if known)
        // Skip fiat deposits, and skip transfers/internal moves that look like deposits
        foreach (var dep in ledger.Where(e => e.Type == "deposit" && e.Amount > 0 && !e.IsFiat
                                              && !StakingTransferSubtypes.Contains(e.SubType)))
        {
            var gbpValue = _fxService.GetGbpValueOfAsset(dep.NormalisedAsset, dep.Amount, dep.DateTime);
            events.Add(new CgtEvent
            {
                Date = dep.DateTime,
                Asset = dep.NormalisedAsset,
                IsAcquisition = true,
                Quantity = dep.Amount,
                Fee = dep.Fee,
                GbpValue = gbpValue,
                RefId = dep.RefId,
                LedgerId = dep.LedgerId
            });

            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Info,
                Category = "Deposit",
                Message = $"Deposit of {dep.Amount} {dep.NormalisedAsset} valued at market rate ({gbpValue:£#,##0.00}). If transferred from another wallet, the actual cost basis may differ.",
                Date = dep.DateTime,
                Asset = dep.NormalisedAsset,
                LedgerId = dep.LedgerId
            });
        }

        // Staking rewards = acquisition at GBP value on date received
        // (taxed as income, but also establishes cost basis for future disposal)
        // Includes staking, dividend, and reward types
        foreach (var stake in ledger.Where(e => e.Type is "staking" or "dividend" or "reward" && e.Amount > 0))
        {
            var gbpValue = _fxService.GetGbpValueOfAsset(stake.NormalisedAsset, stake.Amount, stake.DateTime);
            events.Add(new CgtEvent
            {
                Date = stake.DateTime,
                Asset = stake.NormalisedAsset,
                IsAcquisition = true,
                Quantity = stake.Amount,
                Fee = stake.Fee,
                GbpValue = gbpValue,
                RefId = stake.RefId,
                LedgerId = stake.LedgerId
            });
        }

        return events.OrderBy(e => e.Date).ToList();
    }

    private List<DisposalRecord> CalculateDisposals(List<CgtEvent> events)
    {
        var disposals = new List<DisposalRecord>();

        var acquisitions = events.Where(e => e.IsAcquisition).ToList();
        var disposalEvents = events.Where(e => !e.IsAcquisition).ToList();

        // Track remaining quantity per acquisition for same-day / B&B matching
        var remainingAcq = acquisitions.Select(a => new AcqRemaining
        {
            Event = a,
            RemainingQty = a.Quantity
        }).ToList();

        // Section 104 pools per asset
        var pools = new Dictionary<string, Section104Pool>(StringComparer.OrdinalIgnoreCase);

        // First pass: same-day and B&B matching
        foreach (var disposal in disposalEvents.OrderBy(d => d.Date))
        {
            var asset = disposal.Asset;
            var remainingQty = disposal.Quantity;
            var disposalDate = disposal.Date.Date;

            // === RULE 1: Same-day matching ===
            var sameDayAcqs = remainingAcq
                .Where(a => a.Event.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)
                         && a.Event.Date.Date == disposalDate
                         && a.RemainingQty > 0)
                .ToList();

            foreach (var acq in sameDayAcqs)
            {
                if (remainingQty <= 0) break;

                var matchQty = Math.Min(remainingQty, acq.RemainingQty);
                var costProportion = acq.Event.Quantity > 0
                    ? (matchQty / acq.Event.Quantity) * acq.Event.GbpValue : 0;
                var proceedsProportion = disposal.Quantity > 0
                    ? (matchQty / disposal.Quantity) * disposal.GbpValue : 0;

                disposals.Add(new DisposalRecord
                {
                    Asset = asset,
                    Date = disposal.Date,
                    QuantityDisposed = matchQty,
                    DisposalProceeds = proceedsProportion,
                    AllowableCost = costProportion,
                    MatchingRule = "Same Day",
                    TradeId = disposal.RefId,
                    TaxYear = GetTaxYearLabel(disposal.Date)
                });

                acq.RemainingQty -= matchQty;
                remainingQty -= matchQty;
            }

            // === RULE 2: Bed & Breakfast (30-day) rule ===
            if (remainingQty > 0)
            {
                var bnbAcqs = remainingAcq
                    .Where(a => a.Event.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)
                             && a.Event.Date.Date > disposalDate
                             && a.Event.Date.Date <= disposalDate.AddDays(30)
                             && a.RemainingQty > 0)
                    .OrderBy(a => a.Event.Date)
                    .ToList();

                foreach (var acq in bnbAcqs)
                {
                    if (remainingQty <= 0) break;

                    var matchQty = Math.Min(remainingQty, acq.RemainingQty);
                    var costProportion = acq.Event.Quantity > 0
                        ? (matchQty / acq.Event.Quantity) * acq.Event.GbpValue : 0;
                    var proceedsProportion = disposal.Quantity > 0
                        ? (matchQty / disposal.Quantity) * disposal.GbpValue : 0;

                    disposals.Add(new DisposalRecord
                    {
                        Asset = asset,
                        Date = disposal.Date,
                        QuantityDisposed = matchQty,
                        DisposalProceeds = proceedsProportion,
                        AllowableCost = costProportion,
                        MatchingRule = "Bed & Breakfast",
                        TradeId = disposal.RefId,
                        TaxYear = GetTaxYearLabel(disposal.Date)
                    });

                    acq.RemainingQty -= matchQty;
                    remainingQty -= matchQty;
                }
            }
        }

        // Build consumed quantities map for Section 104
        var consumedQty = new Dictionary<CgtEvent, decimal>();
        foreach (var ra in remainingAcq)
        {
            var consumed = ra.Event.Quantity - ra.RemainingQty;
            if (consumed > 0) consumedQty[ra.Event] = consumed;
        }

        var matchedDisposalQty = disposals
            .GroupBy(d => (d.TradeId, d.Date))
            .ToDictionary(g => g.Key, g => g.Sum(d => d.QuantityDisposed));

        // Second pass: Section 104 pool
        foreach (var evt in events.OrderBy(e => e.Date).ThenBy(e => e.IsAcquisition ? 0 : 1))
        {
            var asset = evt.Asset;
            if (!pools.ContainsKey(asset))
                pools[asset] = new Section104Pool { Asset = asset };

            var pool = pools[asset];

            if (evt.IsAcquisition)
            {
                var consumed = consumedQty.GetValueOrDefault(evt, 0m);
                var poolQty = evt.Quantity - consumed;
                if (poolQty > 0)
                {
                    var poolCost = evt.Quantity > 0
                        ? (poolQty / evt.Quantity) * evt.GbpValue : 0;
                    pool.AddTokens(poolQty, poolCost);
                }
            }
            else
            {
                var key = (evt.RefId, evt.Date);
                var alreadyMatched = matchedDisposalQty.GetValueOrDefault(key, 0m);
                var poolQty = evt.Quantity - alreadyMatched;

                if (poolQty > 0)
                {
                    if (poolQty > pool.Quantity && pool.Quantity > 0)
                    {
                        _warnings.Add(new CalculationWarning
                        {
                            Level = WarningLevel.Warning,
                            Category = "Pool",
                            Message = $"Disposing {poolQty:0.########} {asset} but Section 104 pool only contains {pool.Quantity:0.########}. " +
                                      $"This may indicate missing acquisition data (e.g. transfers from another exchange/wallet).",
                            Date = evt.Date,
                            Asset = asset,
                            LedgerId = evt.LedgerId
                        });
                    }

                    if (pool.Quantity <= 0)
                    {
                        _warnings.Add(new CalculationWarning
                        {
                            Level = WarningLevel.Error,
                            Category = "Pool",
                            Message = $"Disposing {poolQty:0.########} {asset} but Section 104 pool is empty (0 quantity). " +
                                      $"Cost basis will be £0 — this likely means acquisition data is missing.",
                            Date = evt.Date,
                            Asset = asset,
                            LedgerId = evt.LedgerId
                        });
                    }

                    var actualQty = Math.Min(poolQty, Math.Max(pool.Quantity, 0));
                    decimal costFromPool = 0;
                    if (actualQty > 0)
                        costFromPool = pool.RemoveTokens(actualQty);

                    var proceedsProportion = evt.Quantity > 0
                        ? (poolQty / evt.Quantity) * evt.GbpValue : 0;

                    disposals.Add(new DisposalRecord
                    {
                        Asset = asset,
                        Date = evt.Date,
                        QuantityDisposed = poolQty,
                        DisposalProceeds = proceedsProportion,
                        AllowableCost = costFromPool,
                        MatchingRule = "Section 104",
                        TradeId = evt.RefId,
                        TaxYear = GetTaxYearLabel(evt.Date)
                    });
                }
            }
        }

        return disposals.OrderBy(d => d.Date).ToList();
    }

    /// <summary>
    /// Replays all ledger entries chronologically to compute running balances per normalised asset.
    /// Captures snapshots at each tax year boundary (6 April 00:00 UTC).
    /// Returns a dictionary keyed by tax year label (e.g. "2023/24") with start and end snapshots.
    /// </summary>
    private Dictionary<string, (BalanceSnapshot Start, BalanceSnapshot End)> ComputeBalanceSnapshots(
        List<KrakenLedgerEntry> ledger)
    {
        var result = new Dictionary<string, (BalanceSnapshot Start, BalanceSnapshot End)>();
        if (ledger.Count == 0) return result;

        // Running balances per normalised asset
        var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        // Sort all entries by time
        var sorted = ledger.OrderBy(e => e.Time).ToList();

        // Determine the range of tax years we need snapshots for
        var earliest = sorted.First().DateTime;
        var latest = sorted.Last().DateTime;

        int firstTaxYearStart = earliest.Month < 4 || (earliest.Month == 4 && earliest.Day <= 5)
            ? earliest.Year - 1 : earliest.Year;
        int lastTaxYearStart = latest.Month < 4 || (latest.Month == 4 && latest.Day <= 5)
            ? latest.Year - 1 : latest.Year;

        // Build ordered list of tax year boundary dates (6 April of each year)
        var boundaries = new SortedList<DateTimeOffset, string>();
        for (int year = firstTaxYearStart; year <= lastTaxYearStart + 1; year++)
        {
            var boundaryDate = new DateTimeOffset(year, 4, 6, 0, 0, 0, TimeSpan.Zero);
            var label = $"{year}/{(year + 1) % 100:D2}";
            boundaries[boundaryDate] = label;
        }

        // Walk through entries, snapshotting at each boundary we pass
        int entryIdx = 0;
        var boundaryKeys = boundaries.Keys.ToList();

        // Take snapshot at each boundary date
        Dictionary<string, Dictionary<string, decimal>> snapshotAtBoundary = new();

        foreach (var boundary in boundaryKeys)
        {
            // Process all entries up to (but not including) this boundary
            while (entryIdx < sorted.Count && sorted[entryIdx].DateTime < boundary)
            {
                var entry = sorted[entryIdx];
                if (!entry.IsFiat) // Exclude fiat from crypto balance snapshots
                {
                    var asset = entry.NormalisedAsset;
                    if (!string.IsNullOrEmpty(asset))
                    {
                        balances.TryGetValue(asset, out var current);
                        balances[asset] = current + entry.Amount - entry.Fee;
                    }
                }
                entryIdx++;
            }

            // Snapshot current balances at this boundary
            snapshotAtBoundary[boundary.ToString("o")] = new Dictionary<string, decimal>(balances);
        }

        // Process remaining entries after the last boundary
        while (entryIdx < sorted.Count)
        {
            var entry = sorted[entryIdx];
            if (!entry.IsFiat)
            {
                var asset = entry.NormalisedAsset;
                if (!string.IsNullOrEmpty(asset))
                {
                    balances.TryGetValue(asset, out var current);
                    balances[asset] = current + entry.Amount - entry.Fee;
                }
            }
            entryIdx++;
        }

        // Build start/end snapshots for each tax year
        for (int i = 0; i < boundaryKeys.Count - 1; i++)
        {
            var startBoundary = boundaryKeys[i];
            var endBoundary = boundaryKeys[i + 1];
            var taxYearLabel = boundaries[startBoundary];

            var startBalances = snapshotAtBoundary[startBoundary.ToString("o")];
            var endBalances = snapshotAtBoundary[endBoundary.ToString("o")];

            var startSnapshot = new BalanceSnapshot
            {
                Label = $"Start of {taxYearLabel}",
                Date = startBoundary,
                Balances = startBalances
                    .Where(kv => kv.Value > 0.00000001m)
                    .OrderByDescending(kv => _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, startBoundary))
                    .Select(kv => new AssetBalance
                    {
                        Asset = kv.Key,
                        Quantity = kv.Value,
                        GbpValue = _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, startBoundary)
                    }).ToList()
            };

            var endSnapshot = new BalanceSnapshot
            {
                Label = $"End of {taxYearLabel}",
                Date = endBoundary.AddDays(-1), // 5 April
                Balances = endBalances
                    .Where(kv => kv.Value > 0.00000001m)
                    .OrderByDescending(kv => _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, endBoundary.AddDays(-1)))
                    .Select(kv => new AssetBalance
                    {
                        Asset = kv.Key,
                        Quantity = kv.Value,
                        GbpValue = _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, endBoundary.AddDays(-1))
                    }).ToList()
            };

            result[taxYearLabel] = (startSnapshot, endSnapshot);
        }

        // Handle the final tax year (from last boundary to end of data)
        if (boundaryKeys.Count > 0)
        {
            var lastBoundary = boundaryKeys[^1];
            var lastLabel = boundaries[lastBoundary];

            // Only add if we have entries in this tax year and it's not already covered
            if (!result.ContainsKey(lastLabel) && latest >= lastBoundary)
            {
                var startBalances = snapshotAtBoundary[lastBoundary.ToString("o")];

                var startSnapshot = new BalanceSnapshot
                {
                    Label = $"Start of {lastLabel}",
                    Date = lastBoundary,
                    Balances = startBalances
                        .Where(kv => kv.Value > 0.00000001m)
                        .OrderByDescending(kv => _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, lastBoundary))
                        .Select(kv => new AssetBalance
                        {
                            Asset = kv.Key,
                            Quantity = kv.Value,
                            GbpValue = _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, lastBoundary)
                        }).ToList()
                };

                // End = current balances (latest data point)
                var endSnapshot = new BalanceSnapshot
                {
                    Label = $"End of {lastLabel} (latest data)",
                    Date = latest,
                    Balances = balances
                        .Where(kv => kv.Value > 0.00000001m)
                        .OrderByDescending(kv => _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, latest))
                        .Select(kv => new AssetBalance
                        {
                            Asset = kv.Key,
                            Quantity = kv.Value,
                            GbpValue = _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, latest)
                        }).ToList()
                };

                result[lastLabel] = (startSnapshot, endSnapshot);
            }
        }

        return result;
    }

    private List<TaxYearSummary> BuildTaxYearSummaries(
        List<DisposalRecord> disposals,
        List<KrakenLedgerEntry> stakingEntries,
        Dictionary<string, TaxYearUserInput> userInputs,
        Dictionary<string, (BalanceSnapshot Start, BalanceSnapshot End)>? balanceSnapshots = null)
    {
        // Get all tax years from disposals, staking, and balance snapshots
        var allTaxYears = new HashSet<string>();
        foreach (var d in disposals)
            allTaxYears.Add(d.TaxYear);
        foreach (var s in stakingEntries)
            allTaxYears.Add(GetTaxYearLabel(s.DateTime));
        if (balanceSnapshots != null)
            foreach (var key in balanceSnapshots.Keys)
                allTaxYears.Add(key);

        var summaries = new List<TaxYearSummary>();

        foreach (var taxYearLabel in allTaxYears.OrderBy(y => y))
        {
            var startYear = int.Parse(taxYearLabel.Split('/')[0]);
            var rates = UkTaxRates.GetRatesForYear(startYear);
            var userInput = userInputs.GetValueOrDefault(taxYearLabel, new TaxYearUserInput());

            var yearDisposals = disposals.Where(d => d.TaxYear == taxYearLabel).ToList();
            var totalProceeds = yearDisposals.Sum(d => d.DisposalProceeds);
            var totalCosts = yearDisposals.Sum(d => d.AllowableCost);
            var totalGains = yearDisposals.Where(d => d.GainOrLoss > 0).Sum(d => d.GainOrLoss);
            var totalLosses = yearDisposals.Where(d => d.GainOrLoss < 0).Sum(d => d.GainOrLoss);
            var netGain = totalGains + totalLosses;

            // Staking income for this tax year
            var yearStaking = stakingEntries
                .Where(s => GetTaxYearLabel(s.DateTime) == taxYearLabel)
                .ToList();

            var stakingIncome = 0m;
            var stakingDetails = new List<StakingReward>();
            foreach (var s in yearStaking)
            {
                var gbpValue = _fxService.GetGbpValueOfAsset(s.NormalisedAsset, s.Amount, s.DateTime);
                stakingIncome += gbpValue;
                stakingDetails.Add(new StakingReward
                {
                    Date = s.DateTime,
                    Asset = s.NormalisedAsset,
                    Amount = s.Amount,
                    GbpValue = gbpValue
                });
            }

            // CGT calculation
            var otherGains = userInput.OtherCapitalGains;
            var totalNetGain = netGain + otherGains;
            var taxableGain = Math.Max(0, totalNetGain - rates.AnnualExemptAmount);
            var taxableIncome = userInput.TaxableIncome;
            var cgtDue = CalculateCgt(taxableGain, taxableIncome, rates);

            // Warnings for this tax year
            var yearWarnings = _warnings
                .Where(w => w.Date.HasValue && GetTaxYearLabel(w.Date.Value) == taxYearLabel)
                .ToList();
            // Also include undated warnings
            yearWarnings.AddRange(_warnings.Where(w => !w.Date.HasValue));

            // Balance snapshots for this tax year
            var startBalances = new BalanceSnapshot();
            var endBalances = new BalanceSnapshot();
            if (balanceSnapshots != null && balanceSnapshots.TryGetValue(taxYearLabel, out var snapshots))
            {
                startBalances = snapshots.Start;
                endBalances = snapshots.End;
            }

            summaries.Add(new TaxYearSummary
            {
                TaxYear = taxYearLabel,
                StartYear = startYear,
                TaxableIncome = taxableIncome,
                OtherCapitalGains = otherGains,
                Disposals = yearDisposals,
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
                PersonalAllowance = rates.PersonalAllowance,
                StakingIncome = stakingIncome,
                StakingRewards = stakingDetails,
                StartOfYearBalances = startBalances,
                EndOfYearBalances = endBalances,
                Warnings = yearWarnings
            });
        }

        return summaries;
    }

    private decimal CalculateCgt(decimal taxableGain, decimal taxableIncome, UkTaxRates rates)
    {
        if (taxableGain <= 0) return 0;

        var incomeAbovePA = Math.Max(0, taxableIncome - rates.PersonalAllowance);
        var unusedBasicBand = Math.Max(0, rates.BasicRateBand - incomeAbovePA);

        var gainsAtBasicRate = Math.Min(taxableGain, unusedBasicBand);
        var gainsAtHigherRate = taxableGain - gainsAtBasicRate;

        return (gainsAtBasicRate * rates.BasicRateCgt) + (gainsAtHigherRate * rates.HigherRateCgt);
    }

    public static string GetTaxYearLabel(DateTimeOffset date)
    {
        int year = date.Year;
        if (date.Month < 4 || (date.Month == 4 && date.Day <= 5))
            year--;

        int endYear = (year + 1) % 100;
        return $"{year}/{endYear:D2}";
    }

    private class CgtEvent
    {
        public DateTimeOffset Date { get; set; }
        public string Asset { get; set; } = "";
        public bool IsAcquisition { get; set; }
        public decimal Quantity { get; set; }
        public decimal Fee { get; set; }
        public decimal GbpValue { get; set; }
        public string RefId { get; set; } = "";
        public string LedgerId { get; set; } = "";
    }

    private class AcqRemaining
    {
        public CgtEvent Event { get; set; } = null!;
        public decimal RemainingQty { get; set; }
    }
}
