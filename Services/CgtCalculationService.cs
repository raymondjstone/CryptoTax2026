using System;
using System.Collections.Generic;
using System.Linq;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

public class CgtCalculationService
{
    private readonly FxConversionService _fxService;
    private readonly List<CalculationWarning> _warnings;
    private readonly List<KrakenTrade> _trades;

    public CgtCalculationService(FxConversionService fxService, List<CalculationWarning> warnings, List<KrakenTrade>? trades = null)
    {
        _fxService = fxService;
        _warnings = warnings;
        _trades = trades ?? new();
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

    // Ledger subtypes that are staking-related internal moves (not taxable).
    // Covers classic Kraken staking, futures, Earn (flex/bonded), and parachain bonding.
    private static readonly HashSet<string> StakingTransferSubtypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Classic staking ↔ spot
        "spotfromstaking", "stakingfromspot", "spottostaking", "stakingtospot",
        // Futures ↔ spot
        "spotfromfutures", "futuresfromspot", "spottofutures", "futurestospot",
        // Kraken Earn flex product ↔ spot
        "spotfromearnflex", "earnflexfromspot", "spottoearnflex", "earnflextospotspot",
        "earnfromspot", "spotfromearn", "spottoearn", "earntospot",
        // Bonded staking ↔ spot
        "spotfrombonding", "bondingfromspot", "spottobonding", "bondingtospot",
        // Parachain crowdloan/slot auction
        "spotfromparachain", "parachainfromspot", "spottoparachain", "parachaintospot",
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
        // Include "spend" and "receive" types alongside "trade" — Kraken records some
        // transactions (Instant Buy, Convert) with one leg as "spend" and the other as "receive"
        // rather than both as "trade". They share the same refid, so grouping all three
        // types together correctly reconstructs the full trade.
        // Include "conversion" — Kraken records some token migrations (e.g. MATIC→POL) as conversions.
        // Include "adjustment" — Kraken also records token migrations as paired balance adjustments
        // (e.g. MATIC −8171 / POL +8171, same refid). Both types reach ProcessTradeGroup where
        // the distinctAssets guard returns early for same-asset cases and handles cross-asset pairs
        // as disposal + acquisition so the S104 pool transfers correctly.
        // Include "sale" — Kraken Instant Sell records the crypto leg as type=sale in some regions.
        var filteredByRefId = ledger
            .Where(e => e.Type == "trade" || e.Type == "spend" || e.Type == "receive"
                     || e.Type == "conversion" || e.Type == "adjustment" || e.Type == "sale")
            .Where(e => !StakingTransferSubtypes.Contains(e.SubType))
            .GroupBy(e => e.RefId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Time).ToList(), StringComparer.OrdinalIgnoreCase);

        // Build order-level lookups from trade history so we can resolve partial-fill ledger gaps.
        // When a large order fills simultaneously across multiple price levels, Kraken sometimes
        // creates one combined quote-side entry (GBP) for the whole order while creating individual
        // crypto entries per fill — leaving some fills with only 1 ledger entry.
        // Grouping by ordertxid recovers the full picture.
        var tradeById = _trades.ToDictionary(t => t.TradeId, t => t, StringComparer.OrdinalIgnoreCase);
        var orderToFillIds = _trades
            .Where(t => !string.IsNullOrEmpty(t.OrderTxId))
            .GroupBy(t => t.OrderTxId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => t.TradeId).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        // Pre-pass: find refids that need order-level grouping and build combined entry sets.
        var refIdsHandledAtOrderLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderLevelGroups = new List<(string OrderId, List<KrakenLedgerEntry> Entries)>();

        foreach (var (refId, refEntries) in filteredByRefId)
        {
            if (refEntries.Count >= 2) continue; // sufficient as-is

            // Check full ledger (all types) for this refid before escalating to order-level.
            // Exclude type="transfer" — staking moves (POL→POL.F etc.) share refids and must not
            // be treated as trade legs.
            var fullLegs = ledger
                .Where(e => e.RefId == refId && e.Type != "transfer" && !StakingTransferSubtypes.Contains(e.SubType))
                .ToList();
            if (fullLegs.Count >= 2) continue; // resolved by full-ledger search

            // Single-entry fill — look up which order it belongs to
            if (!tradeById.TryGetValue(refId, out var trade) || string.IsNullOrEmpty(trade.OrderTxId))
                continue; // no trade data — will warn in the main loop

            var orderId = trade.OrderTxId;
            if (refIdsHandledAtOrderLevel.Contains(refId)) continue; // already queued

            if (!orderToFillIds.TryGetValue(orderId, out var allFillIds)) continue;

            // Collect all ledger entries for every fill of this order.
            // Exclude type="transfer" to prevent staking move entries from being treated as trade legs.
            var combined = allFillIds
                .SelectMany(id =>
                    filteredByRefId.TryGetValue(id, out var l)
                        ? l
                        : ledger.Where(e => e.RefId == id && e.Type != "transfer" && !StakingTransferSubtypes.Contains(e.SubType)))
                .OrderBy(e => e.Time)
                .ToList();

            if (combined.Count < 2) continue; // still not enough — will warn per-refid

            orderLevelGroups.Add((orderId, combined));
            foreach (var fillId in allFillIds)
                refIdsHandledAtOrderLevel.Add(fillId);
        }

        // Process order-level groups first (multi-fill orders with some incomplete ledger entries)
        foreach (var (orderId, orderEntries) in orderLevelGroups)
            ProcessTradeGroup(orderEntries, orderId, events);

        // Process individual refid groups (skip those handled at order level above)
        foreach (var (refId, entries) in filteredByRefId)
        {
            if (refIdsHandledAtOrderLevel.Contains(refId))
                continue;

            var entryList = entries;
            if (entryList.Count < 2)
            {
                // The other leg may have a different type (e.g. adjustment, conversion).
                // Search the full ledger for any entry with the same refid.
                // Exclude type="transfer" — staking moves must not be treated as trade legs.
                var allLegs = ledger
                    .Where(e => e.RefId == refId && e.Type != "transfer" && !StakingTransferSubtypes.Contains(e.SubType))
                    .OrderBy(e => e.Time)
                    .ToList();

                if (allLegs.Count >= 2)
                    entryList = allLegs;
                else
                {
                    _warnings.Add(new CalculationWarning
                    {
                        Level = WarningLevel.Warning,
                        Category = "Ledger",
                        Message = $"Trade refid {refId} has only {entryList.Count} ledger entry (expected 2+). Skipping.\n" +
                                  string.Join("\n", entryList.Select(e =>
                                      $"  [{e.LedgerId}] {e.DateTime:dd/MM/yyyy HH:mm} | type={e.Type} | {e.NormalisedAsset} amount={e.Amount} fee={e.Fee}")),
                        Date = entryList.FirstOrDefault()?.DateTime,
                        LedgerId = entryList.FirstOrDefault()?.LedgerId
                    });
                    continue;
                }
            }

            ProcessTradeGroup(entryList, refId, events);
        }

        // Delisting conversions (e.g. MATIC→POL token migration).
        // Kraken records these as type=transfer, subtype=delistingconversion with separate refids
        // for the old-token disposal and new-token acquisition. Handle each side independently:
        // positive amount = acquisition of new token, negative = disposal of old token.
        foreach (var entry in ledger.Where(e => e.Type == "transfer"
                     && string.Equals(e.SubType, "delistingconversion", StringComparison.OrdinalIgnoreCase)
                     && !e.IsFiat))
        {
            var qty = Math.Abs(entry.Amount);
            var gbpValue = _fxService.GetGbpValueOfAsset(entry.NormalisedAsset, qty, entry.DateTime);
            events.Add(new CgtEvent
            {
                Date = entry.DateTime,
                Asset = entry.NormalisedAsset,
                IsAcquisition = entry.Amount > 0,
                Quantity = qty,
                Fee = entry.Fee,
                GbpValue = gbpValue,
                RefId = entry.RefId,
                LedgerId = entry.LedgerId
            });

            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Info,
                Category = "Conversion",
                Message = entry.Amount > 0
                    ? $"Delisting conversion: acquired {qty} {entry.NormalisedAsset} valued at {gbpValue:£#,##0.00}."
                    : $"Delisting conversion: disposed {qty} {entry.NormalisedAsset} valued at {gbpValue:£#,##0.00}.",
                Date = entry.DateTime,
                Asset = entry.NormalisedAsset,
                LedgerId = entry.LedgerId
            });
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

    /// <summary>
    /// Processes a list of ledger entries (one or more fills of the same trade/order) and
    /// appends the resulting CGT acquisition/disposal events to <paramref name="events"/>.
    /// For multi-fill orders, all fills are aggregated: GBP values are summed across all
    /// GBP entries, and each crypto asset's quantity is summed across all fills.
    /// </summary>
    private void ProcessTradeGroup(List<KrakenLedgerEntry> entries, string groupId, List<CgtEvent> events)
    {
        // Detect flex staking / internal transfers disguised as "trades":
        // Both sides normalise to the same asset (e.g. ETH → XETH.F or SOL → SOL.F).
        var distinctAssets = entries
            .Select(e => e.NormalisedAsset)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctAssets.Count == 1)
            return; // Same asset on both sides — staking transfer, not a trade

        var received = entries.Where(e => e.Amount > 0).ToList();
        var spent    = entries.Where(e => e.Amount < 0).ToList();

        if (received.Count == 0 || spent.Count == 0)
        {
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Info,
                Category = "Ledger",
                Message = $"Trade {groupId}: no clear buy/sell sides.\n" +
                          string.Join("\n", entries.Select(e =>
                              $"  [{e.LedgerId}] {e.DateTime:dd/MM/yyyy HH:mm} | type={e.Type} | {e.NormalisedAsset} amount={e.Amount} fee={e.Fee}")),
                Date = entries.First().DateTime,
                LedgerId = entries.First().LedgerId
            });
            return;
        }

        var date = entries.First().DateTime;

        decimal tradeGbpValue = 0;
        bool hasDirectGbp = false;

        // Sum all GBP entries — a multi-fill order may have several GBP debit entries
        var gbpEntries = entries.Where(e => e.NormalisedAsset == "GBP").ToList();
        if (gbpEntries.Count > 0)
        {
            tradeGbpValue = gbpEntries.Sum(e => Math.Abs(e.Amount) - e.Fee);
            hasDirectGbp = true;
        }

        // Sum all other fiat entries (USD, EUR, etc.)
        if (!hasDirectGbp)
        {
            var fiatEntries = entries.Where(e => e.IsFiat).ToList();
            if (fiatEntries.Count > 0)
            {
                foreach (var fe in fiatEntries)
                    tradeGbpValue += _fxService.ConvertToGbp(Math.Abs(fe.Amount) - fe.Fee, fe.NormalisedAsset, date);
                hasDirectGbp = true;
            }
        }

        // Sum all stablecoin entries
        if (!hasDirectGbp)
        {
            var stableEntries = entries.Where(e => e.NormalisedAsset is "USDT" or "USDC" or "DAI").ToList();
            if (stableEntries.Count > 0)
            {
                foreach (var se in stableEntries)
                    tradeGbpValue += _fxService.ConvertToGbp(Math.Abs(se.Amount) - se.Fee, se.NormalisedAsset, date);
                hasDirectGbp = true;
            }
        }

        // Purely crypto-to-crypto — value using the disposal side
        if (!hasDirectGbp)
        {
            foreach (var s in spent)
                tradeGbpValue += _fxService.ConvertToGbp(Math.Abs(s.Amount), s.NormalisedAsset, date);
        }

        // Disposals — group by asset and sum quantities across fills
        var spentByCryptoAsset = spent
            .Where(e => !e.IsFiat && e.NormalisedAsset is not "USDT" and not "USDC" and not "DAI")
            .GroupBy(e => e.NormalisedAsset, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalSpentCrypto = spentByCryptoAsset.Sum(g => g.Sum(e => Math.Abs(e.Amount)));

        foreach (var assetGroup in spentByCryptoAsset)
        {
            var qty = assetGroup.Sum(e => Math.Abs(e.Amount));
            var fee = assetGroup.Sum(e => e.Fee);
            var proportion = totalSpentCrypto > 0 ? qty / totalSpentCrypto : 1m;

            events.Add(new CgtEvent
            {
                Date    = date,
                Asset   = assetGroup.Key,
                IsAcquisition = false,
                Quantity = qty,
                Fee      = fee,
                GbpValue = tradeGbpValue * proportion,
                RefId    = groupId,
                LedgerId = assetGroup.First().LedgerId
            });
        }

        // Acquisitions — group by asset and sum quantities across fills
        var receivedByCryptoAsset = received
            .Where(e => !e.IsFiat && e.NormalisedAsset is not "USDT" and not "USDC" and not "DAI")
            .GroupBy(e => e.NormalisedAsset, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalReceivedCrypto = receivedByCryptoAsset.Sum(g => g.Sum(e => e.Amount));

        foreach (var assetGroup in receivedByCryptoAsset)
        {
            var qty = assetGroup.Sum(e => e.Amount);
            var fee = assetGroup.Sum(e => e.Fee);
            var proportion = totalReceivedCrypto > 0 ? qty / totalReceivedCrypto : 1m;

            events.Add(new CgtEvent
            {
                Date    = date,
                Asset   = assetGroup.Key,
                IsAcquisition = true,
                Quantity = qty,
                Fee      = fee,
                GbpValue = (tradeGbpValue * proportion) + _fxService.ConvertToGbp(fee, assetGroup.Key, date),
                RefId    = groupId,
                LedgerId = assetGroup.First().LedgerId
            });
        }

        // Stablecoin acquisitions/disposals
        foreach (var e in entries.Where(e => e.NormalisedAsset is "USDT" or "USDC" or "DAI"))
        {
            if (e.Amount > 0)
                events.Add(new CgtEvent
                {
                    Date = date, Asset = e.NormalisedAsset, IsAcquisition = true,
                    Quantity = e.Amount, Fee = e.Fee,
                    GbpValue = _fxService.ConvertToGbp(e.Amount, e.NormalisedAsset, date),
                    RefId = groupId, LedgerId = e.LedgerId
                });
            else if (e.Amount < 0)
                events.Add(new CgtEvent
                {
                    Date = date, Asset = e.NormalisedAsset, IsAcquisition = false,
                    Quantity = Math.Abs(e.Amount), Fee = e.Fee,
                    GbpValue = _fxService.ConvertToGbp(Math.Abs(e.Amount), e.NormalisedAsset, date),
                    RefId = groupId, LedgerId = e.LedgerId
                });
        }
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
                    pool.AddTokens(poolQty, poolCost, evt.Date, evt.RefId);
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
                        var historyLines = pool.History.Count > 0
                            ? "\n  Pool contents (acquisitions into pool after same-day/B&B matching):\n" +
                              string.Join("\n", pool.History.Select(h =>
                                  $"    {h.Date:dd/MM/yyyy} | qty={h.Quantity:0.########} | cost=£{h.Cost:#,##0.00} | ref={h.RefId}"))
                            : "";
                        _warnings.Add(new CalculationWarning
                        {
                            Level = WarningLevel.Warning,
                            Category = "Pool",
                            Message = $"Disposing {poolQty:0.########} {asset} but Section 104 pool only contains {pool.Quantity:0.########} " +
                                      $"(pooled cost £{pool.PooledCost:#,##0.00}, avg £{pool.CostPerUnit:#,##0.########}/unit). " +
                                      $"Shortfall: {poolQty - pool.Quantity:0.########} units. " +
                                      $"This may indicate missing acquisition data (e.g. transfers from another exchange/wallet)." +
                                      historyLines,
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
                            Message = $"Disposing {poolQty:0.########} {asset} but Section 104 pool is empty. " +
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
