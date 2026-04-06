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
    private readonly List<DelistedAssetEvent> _delistedAssets;
    private readonly Dictionary<string, decimal> _costBasisOverrides;
    private readonly DateTimeOffset? _nowOverride;

    /// <summary>
    /// After CalculateAllTaxYears runs, holds the final Section 104 pool state per asset.
    /// </summary>
    public Dictionary<string, Section104Pool> FinalPools { get; private set; } = new();

    // Cached intermediate results from the last full calculation.
    // Used by RebuildSummariesOnly to avoid redoing the expensive work.
    private List<DisposalRecord>? _cachedDisposals;
    private List<KrakenLedgerEntry>? _cachedStakingEntries;
    private Dictionary<string, (BalanceSnapshot Start, BalanceSnapshot End)>? _cachedSnapshots;

    public CgtCalculationService(FxConversionService fxService, List<CalculationWarning> warnings, List<KrakenTrade>? trades = null, List<DelistedAssetEvent>? delistedAssets = null, Dictionary<string, decimal>? costBasisOverrides = null, DateTimeOffset? nowOverride = null)
    {
        _fxService = fxService;
        _warnings = warnings;
        _trades = trades ?? new();
        _delistedAssets = delistedAssets ?? new();
        _costBasisOverrides = costBasisOverrides ?? new();
        _nowOverride = nowOverride;
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
        // Apply user-defined delisting events: filter out post-delisting entries and warn
        var effectiveLedger = ApplyDelistingFilter(ledger);

        // Build trade events from ledger entries
        var events = BuildEventsFromLedger(effectiveLedger);

        // Inject zero-value disposal events for delisted assets at the delisting date.
        // These will be processed by CalculateDisposals to remove the asset from the S104 pool.
        // We defer injection until after BuildEventsFromLedger so the pool is populated from
        // pre-delisting entries, then the disposal drains whatever remains.
        InjectDelistingDisposals(events);

        // Separate out staking/reward income (staking, dividend, reward types with positive amount)
        var stakingEntries = effectiveLedger
            .Where(e => e.Type is "staking" or "dividend" or "reward" or "airdrop" or "fork" or "mining" && e.Amount > 0)
            .ToList();

        // Calculate disposals using HMRC rules
        var disposals = CalculateDisposals(events);

        // Apply cost basis overrides
        foreach (var d in disposals)
        {
            if (_costBasisOverrides.TryGetValue(d.TradeId, out var overrideCost))
            {
                d.AllowableCost = overrideCost;
            }
        }

        // Compute balance snapshots at tax year boundaries (uses filtered ledger so delisted
        // assets don't show phantom balances after delisting)
        var snapshots = ComputeBalanceSnapshots(effectiveLedger);

        // Cache intermediate results for lightweight summary-only recalculation
        _cachedDisposals = disposals;
        _cachedStakingEntries = stakingEntries;
        _cachedSnapshots = snapshots;

        // Group by tax year and build summaries
        return BuildTaxYearSummaries(disposals, stakingEntries, userInputs, snapshots);
    }

    /// <summary>
    /// Lightweight recalculation that reuses cached disposals, staking entries, and balance
    /// snapshots from the last full <see cref="CalculateAllTaxYears"/> run. Only rebuilds the
    /// tax year summaries (CGT amounts, loss carry-forward). Use when only user inputs
    /// (taxable income, other capital gains) change — no need to redo disposal matching or FX lookups.
    /// </summary>
    public List<TaxYearSummary>? RebuildSummariesOnly(Dictionary<string, TaxYearUserInput> userInputs)
    {
        if (_cachedDisposals == null || _cachedStakingEntries == null || _cachedSnapshots == null)
            return null; // No cached data — caller should do a full recalculation

        return BuildTaxYearSummaries(_cachedDisposals, _cachedStakingEntries, userInputs, _cachedSnapshots);
    }

    /// <summary>
    /// Filters out ledger entries that occur after a user-defined delisting date for that asset,
    /// but only for events with <c>ClaimType = "Negligible Value"</c>. Events with
    /// <c>ClaimType = "Delisted"</c> are informational (the trading pair was removed from the
    /// exchange) and do not suppress ledger entries \u2014 the underlying asset may still be valid.
    /// Warns about each ignored entry. Returns a new list with post-delisting entries removed.
    /// </summary>
    private List<KrakenLedgerEntry> ApplyDelistingFilter(List<KrakenLedgerEntry> ledger)
    {
        if (_delistedAssets.Count == 0) return ledger;

        // Build lookup: normalised asset → list of (delist, relist?) periods
        // Multiple events can cover the same asset (e.g. LUNAUSD and LUNAEUR both map to LUNA,
        // or a pair that was delisted and later relisted more than once).
        var delistPeriods = new Dictionary<string, List<(DateTimeOffset Delist, DateTimeOffset? Relist, string Notes)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var evt in _delistedAssets)
        {
            // Only Negligible Value claims suppress ledger entries.
            // ClaimType="Delisted" means the trading pair was removed from the exchange;
            // the underlying asset may still be held or traded via other pairs — no CGT effect.
            if (!string.Equals(evt.ClaimType, "Negligible Value", StringComparison.OrdinalIgnoreCase))
                continue;
            var asset = evt.EffectiveAsset;
            if (string.IsNullOrWhiteSpace(asset)) continue;
            if (!delistPeriods.TryGetValue(asset, out var list))
                delistPeriods[asset] = list = new List<(DateTimeOffset, DateTimeOffset?, string)>();
            list.Add((evt.DelistingDate, evt.RelistDate, evt.Notes));
        }

        var filtered = new List<KrakenLedgerEntry>(ledger.Count);
        foreach (var entry in ledger)
        {
            if (!delistPeriods.TryGetValue(entry.NormalisedAsset, out var periods))
            {
                filtered.Add(entry);
                continue;
            }

            // Check whether this entry falls inside any delist period
            bool inDelistPeriod = false;
            DateTimeOffset delistDate = default;
            DateTimeOffset? relistDate = null;
            string delistNotes = "";
            foreach (var (delist, relist, notes) in periods)
            {
                if (entry.DateTime > delist && (relist == null || entry.DateTime < relist))
                {
                    inDelistPeriod = true;
                    delistDate = delist;
                    relistDate = relist;
                    delistNotes = notes;
                    break;
                }
            }

            if (inDelistPeriod)
            {
                var suffix = relistDate.HasValue
                    ? $" Pair relisted on {relistDate.Value:dd/MM/yyyy}."
                    : "";
                _warnings.Add(new CalculationWarning
                {
                    Level = WarningLevel.Warning,
                    Category = "Delisting",
                    Message = $"Ignored post-delisting ledger entry for {entry.NormalisedAsset}: " +
                              $"type={entry.Type}, amount={entry.Amount}, fee={entry.Fee} on {entry.DateTime:dd/MM/yyyy HH:mm}. " +
                              $"Pair marked as delisted on {delistDate:dd/MM/yyyy}." + suffix +
                              (string.IsNullOrWhiteSpace(delistNotes) ? "" : $" ({delistNotes})"),
                    Date = entry.DateTime,
                    Asset = entry.NormalisedAsset,
                    LedgerId = entry.LedgerId
                });
            }
            else
            {
                filtered.Add(entry);
            }
        }

        return filtered;
    }

    /// <summary>
    /// For each <see cref="DelistedAssetEvent"/> with <c>ClaimType = "Negligible Value"</c>,
    /// injects a synthetic disposal event that sells the entire holding at £0 proceeds.
    /// Events with <c>ClaimType = "Delisted"</c> are informational (the trading pair was
    /// removed from the exchange) and do not trigger a CGT disposal — the underlying asset
    /// may still be held or tradeable via other pairs.
    /// When a <see cref="DelistedAssetEvent.RelistDate"/> is present the pair was only
    /// temporarily unavailable, so no £0 disposal is injected for that period.
    /// </summary>
    private void InjectDelistingDisposals(List<CgtEvent> events)
    {
        foreach (var delisting in _delistedAssets)
        {
            // Only Negligible Value claims warrant a £0 disposal.
            // A "Delisted" entry records that a specific trading pair was removed;
            // it does not mean the underlying asset has become worthless.
            if (!string.Equals(delisting.ClaimType, "Negligible Value", StringComparison.OrdinalIgnoreCase))
                continue;

            var asset = delisting.EffectiveAsset;
            if (string.IsNullOrWhiteSpace(asset)) continue;

            // If the pair was relisted the asset came back — no £0 disposal is warranted
            if (delisting.RelistDate.HasValue) continue;

            // Sum all acquisitions and disposals for this asset up to the delisting date
            // to determine the holding at delisting time.
            decimal holding = 0;
            foreach (var evt in events.Where(e => string.Equals(e.Asset, asset, StringComparison.OrdinalIgnoreCase)
                                                  && e.Date <= delisting.DelistingDate))
            {
                holding += evt.IsAcquisition ? evt.Quantity : -evt.Quantity;
            }

            if (holding <= 0)
            {
                _warnings.Add(new CalculationWarning
                {
                    Level = WarningLevel.Info,
                    Category = "Delisting",
                    Message = $"Delisting event for {asset} (pair: {delisting.Pair}) on {delisting.DelistingDate:dd/MM/yyyy}: no holding to dispose" +
                              (string.IsNullOrWhiteSpace(delisting.Notes) ? "." : $" ({delisting.Notes})."),
                    Date = delisting.DelistingDate,
                    Asset = asset
                });
                continue;
            }

            events.Add(new CgtEvent
            {
                Date = delisting.DelistingDate,
                Asset = asset,
                IsAcquisition = false,
                Quantity = holding,
                Fee = 0,
                GbpValue = 0m,
                RefId = $"DELISTING-{asset}-{delisting.Pair}",
                LedgerId = $"DELISTING-{asset}-{delisting.Pair}"
            });

            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Info,
                Category = "Delisting",
                Message = $"Delisting event: {holding} {asset} (pair: {delisting.Pair}) disposed at £0 proceeds on {delisting.DelistingDate:dd/MM/yyyy}. " +
                          $"The loss equals the Section 104 pool cost basis" +
                          (string.IsNullOrWhiteSpace(delisting.Notes) ? "." : $" ({delisting.Notes})."),
                Date = delisting.DelistingDate,
                Asset = asset
            });
        }

        // Re-sort events since we appended delisting disposals
        events.Sort((a, b) => a.Date.CompareTo(b.Date));
    }

    private List<CgtEvent> BuildEventsFromLedger(List<KrakenLedgerEntry> ledger)
    {
        // Heuristic: most ledger entries do not become CGT events (transfers etc.),
        // but using ledger.Count is a safe upper bound and avoids repeated resizing.
        var events = new List<CgtEvent>(ledger.Count);

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

        // Pre-built lookup of ALL non-transfer, non-staking-transfer entries by refid.
        // Used by the fallback paths below instead of scanning the full ledger each time.
        var allByRefId = ledger
            .Where(e => e.Type != "transfer" && !StakingTransferSubtypes.Contains(e.SubType))
            .ToLookup(e => e.RefId, StringComparer.OrdinalIgnoreCase);

        // Pre-pass: find refids that need order-level grouping and build combined entry sets.
        var refIdsHandledAtOrderLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderLevelGroups = new List<(string OrderId, List<KrakenLedgerEntry> Entries)>(Math.Min(filteredByRefId.Count, ledger.Count));

        foreach (var (refId, refEntries) in filteredByRefId)
        {
            if (refEntries.Count >= 2) continue; // sufficient as-is

            // Check full ledger (all types) for this refid before escalating to order-level.
            // Exclude type="transfer" — staking moves (POL→POL.F etc.) share refids and must not
            // be treated as trade legs.
            var fullLegs = allByRefId[refId].ToList();
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
                        : allByRefId[id])
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
                var allLegs = allByRefId[refId].OrderBy(e => e.Time).ToList();

                if (allLegs.Count >= 2)
                    entryList = allLegs;
                else
                {
                    // Check if this is a dust trade (sub-penny GBP value) — Kraken sometimes
                    // records tiny fractional amounts from rounding without a fiat counterpart.
                    // Silently skip these instead of warning.
                    var maxAbsAmount = entryList.Max(e => Math.Abs(e.Amount));
                    var asset = entryList.First().NormalisedAsset;
                    var dustGbp = !entryList.First().IsFiat
                        ? _fxService.GetGbpValueOfAsset(asset, maxAbsAmount, entryList.First().DateTime)
                        : maxAbsAmount;
                    if (dustGbp < 0.01m)
                        continue; // dust — not worth warning about

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
                LedgerId = entry.LedgerId,
                Type = "conversion"
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

        // Complete delisting removal (e.g. K token delisted with zero value).
        // Kraken records these as type=transfer, subtype=removal with negative amount.
        // This is a disposal with 0 pounds proceeds - user can claim the loss based on their cost basis.
        foreach (var entry in ledger.Where(e => e.Type == "transfer"
                     && string.Equals(e.SubType, "removal", StringComparison.OrdinalIgnoreCase)
                     && e.Amount < 0
                     && !e.IsFiat))
        {
            var qty = Math.Abs(entry.Amount);
            events.Add(new CgtEvent
            {
                Date = entry.DateTime,
                Asset = entry.NormalisedAsset,
                IsAcquisition = false,
                Quantity = qty,
                Fee = entry.Fee,
                GbpValue = 0m,
                RefId = entry.RefId,
                LedgerId = entry.LedgerId,
                Type = "removal"
            });

            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Warning,
                Category = "Delisting",
                Message = $"Asset delisted/removed: {qty} {entry.NormalisedAsset} disposed with 0 proceeds. The loss equals your cost basis.",
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
                LedgerId = dep.LedgerId,
                Type = "deposit"
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
        foreach (var stake in ledger.Where(e => e.Type is "staking" or "dividend" or "reward" or "airdrop" or "fork" or "mining" && e.Amount > 0))
        {
            // Kraken records gross reward as Amount; Fee is Kraken's commission (e.g. 20% for flexible staking).
            // The user only receives Amount - Fee, so use the net figure for both quantity and income value.
            var netStakeAmount = stake.Amount - stake.Fee;
            if (netStakeAmount <= 0) continue;
            var gbpValue = _fxService.GetGbpValueOfAsset(stake.NormalisedAsset, netStakeAmount, stake.DateTime);
            events.Add(new CgtEvent
            {
                Date = stake.DateTime,
                Asset = stake.NormalisedAsset,
                IsAcquisition = true,
                Quantity = netStakeAmount,
                Fee = 0,
                GbpValue = gbpValue,
                RefId = stake.RefId,
                LedgerId = stake.LedgerId,
                Type = stake.Type
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

        // Sum all GBP entries — a multi-fill order may have several GBP debit entries.
        // Fee direction matters: when receiving GBP (sell), fee reduces proceeds;
        // when spending GBP (buy), fee increases the total outlay (cost basis).
        var gbpEntries = entries.Where(e => e.NormalisedAsset == "GBP").ToList();
        if (gbpEntries.Count > 0)
        {
            tradeGbpValue = gbpEntries.Sum(e => e.Amount > 0
                ? e.Amount - e.Fee          // Sell: net proceeds = received − fee
                : Math.Abs(e.Amount) + e.Fee); // Buy: total cost = spent + fee
            hasDirectGbp = true;
        }

        // Sum all other fiat entries (USD, EUR, etc.)
        if (!hasDirectGbp)
        {
            var fiatEntries = entries.Where(e => e.IsFiat).ToList();
            if (fiatEntries.Count > 0)
            {
                foreach (var fe in fiatEntries)
                {
                    var fiatValue = fe.Amount > 0
                        ? fe.Amount - fe.Fee
                        : Math.Abs(fe.Amount) + fe.Fee;
                    tradeGbpValue += _fxService.ConvertToGbp(fiatValue, fe.NormalisedAsset, date);
                }
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
                {
                    var stableValue = se.Amount > 0
                        ? se.Amount - se.Fee
                        : Math.Abs(se.Amount) + se.Fee;
                    tradeGbpValue += _fxService.ConvertToGbp(stableValue, se.NormalisedAsset, date);
                }
                hasDirectGbp = true;
            }
        }

        // Purely crypto-to-crypto — value using the disposal side
        if (!hasDirectGbp)
        {
            foreach (var s in spent)
                tradeGbpValue += _fxService.ConvertToGbp(Math.Abs(s.Amount), s.NormalisedAsset, date);

            var spentAssets = spent.Select(e => e.NormalisedAsset).Distinct().ToList();
            var receivedAssets = received.Select(e => e.NormalisedAsset).Distinct().ToList();
            _warnings.Add(new CalculationWarning
            {
                Level = WarningLevel.Info,
                Category = "Crypto-to-Crypto",
                Message = $"Trade {groupId}: crypto-to-crypto swap ({string.Join("+", spentAssets)} → {string.Join("+", receivedAssets)}). " +
                          $"Disposal proceeds valued at £{tradeGbpValue:#,##0.00} using FX rate at trade time.",
                Date = date,
                Asset = spentAssets.FirstOrDefault() ?? "",
                LedgerId = entries.First().LedgerId
            });
        }

        // Disposals — group by asset and sum quantities across fills
        var spentByCryptoAsset = spent
            .Where(e => !e.IsFiat && e.NormalisedAsset is not "USDT" and not "USDC" and not "DAI")
            .GroupBy(e => e.NormalisedAsset, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Total crypto leaving per asset = |amount| + same-asset fee (fee deducted on top of the sold amount).
        var totalSpentCrypto = spentByCryptoAsset.Sum(g => g.Sum(e => Math.Abs(e.Amount) + e.Fee));

        foreach (var assetGroup in spentByCryptoAsset)
        {
            var grossQty = assetGroup.Sum(e => Math.Abs(e.Amount));
            var fee = assetGroup.Sum(e => e.Fee);
            // Total crypto leaving the pool includes both the disposed amount and the same-asset fee.
            var qty = grossQty + fee;
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
                LedgerId = assetGroup.First().LedgerId,
                Type     = "trade"
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
            var grossQty = assetGroup.Sum(e => e.Amount);
            var fee = assetGroup.Sum(e => e.Fee);
            // Net quantity = gross amount minus any crypto-denominated fee (e.g. XRP fee on XRP buys).
            // Kraken ledger: amount = gross received, fee = additionally deducted from that asset.
            // The S104 pool must track net quantity (what actually lands in the account).
            // Cost basis (GbpValue) still includes the fee as an allowable dealing cost.
            var qty = grossQty - fee;
            var proportion = totalReceivedCrypto > 0 ? grossQty / totalReceivedCrypto : 1m;

            events.Add(new CgtEvent
            {
                Date    = date,
                Asset   = assetGroup.Key,
                IsAcquisition = true,
                Quantity = qty,
                Fee      = fee,
                GbpValue = (tradeGbpValue * proportion) + _fxService.ConvertToGbp(fee, assetGroup.Key, date),
                RefId    = groupId,
                LedgerId = assetGroup.First().LedgerId,
                Type     = "trade"
            });
        }

        // Stablecoin acquisitions/disposals
        foreach (var e in entries.Where(e => e.NormalisedAsset is "USDT" or "USDC" or "DAI"))
        {
            if (e.Amount > 0)
                // Net received = gross amount minus same-asset fee.
                // GbpValue (total cost) is computed on the gross amount and remains unchanged.
                events.Add(new CgtEvent
                {
                    Date = date, Asset = e.NormalisedAsset, IsAcquisition = true,
                    Quantity = e.Amount - e.Fee, Fee = e.Fee,
                    GbpValue = _fxService.ConvertToGbp(e.Amount, e.NormalisedAsset, date),
                    RefId = groupId, LedgerId = e.LedgerId, Type = "trade"
                });
            else if (e.Amount < 0)
                // Total leaving = disposed amount + same-asset fee.
                // GbpValue (proceeds) is computed on the sold amount only and remains unchanged.
                events.Add(new CgtEvent
                {
                    Date = date, Asset = e.NormalisedAsset, IsAcquisition = false,
                    Quantity = Math.Abs(e.Amount) + e.Fee, Fee = e.Fee,
                    GbpValue = _fxService.ConvertToGbp(Math.Abs(e.Amount), e.NormalisedAsset, date),
                    RefId = groupId, LedgerId = e.LedgerId, Type = "trade"
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

        // Index acquisitions by normalised asset for O(1) lookup instead of O(n) per disposal
        var acqByAsset = new Dictionary<string, List<AcqRemaining>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ra in remainingAcq)
        {
            if (!acqByAsset.TryGetValue(ra.Event.Asset, out var list))
            {
                list = new List<AcqRemaining>();
                acqByAsset[ra.Event.Asset] = list;
            }
            list.Add(ra);
        }

        // Section 104 pools per asset
        var pools = new Dictionary<string, Section104Pool>(StringComparer.OrdinalIgnoreCase);

        // First pass: same-day and B&B matching
        foreach (var disposal in disposalEvents.OrderBy(d => d.Date))
        {
            var asset = disposal.Asset;
            var remainingQty = disposal.Quantity;
            var disposalDate = disposal.Date.Date;

            if (!acqByAsset.TryGetValue(asset, out var assetAcqs))
                assetAcqs = null;

            // === RULE 1: Same-day matching ===
            var sameDayAcqs = assetAcqs?
                .Where(a => a.Event.Date.Date == disposalDate
                         && a.RemainingQty > 0)
                .ToList() ?? [];

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
                var bnbAcqs = assetAcqs?
                    .Where(a => a.Event.Date.Date > disposalDate
                             && a.Event.Date.Date <= disposalDate.AddDays(30)
                             && a.RemainingQty > 0)
                    .OrderBy(a => a.Event.Date)
                    .ToList() ?? [];

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
                        AcquisitionRefId = acq.Event.RefId,
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
                    pool.AddTokens(poolQty, poolCost, evt.Date, evt.RefId, evt.Type);
                }
            }
            else
            {
                var key = (evt.RefId, evt.Date);
                var alreadyMatched = matchedDisposalQty.GetValueOrDefault(key, 0m);
                var poolQty = evt.Quantity - alreadyMatched;

                if (poolQty > 0)
                {
                    // Snap to pool quantity when the shortfall is negligible (< 0.001 units).
                    // Accumulated decimal rounding across many trades with crypto-denominated fees
                    // can create tiny phantom shortfalls that aren't real missing data.
                    if (poolQty > pool.Quantity && pool.Quantity > 0)
                    {
                        var shortfall = poolQty - pool.Quantity;
                        if (shortfall < 0.001m)
                        {
                            poolQty = pool.Quantity;
                        }
                        else
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
                                          $"Shortfall: {shortfall:0.########} units. " +
                                          $"This may indicate missing acquisition data (e.g. transfers from another exchange/wallet)." +
                                          historyLines,
                                Date = evt.Date,
                                Asset = asset,
                                LedgerId = evt.LedgerId
                            });
                        }
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

        // Store final pool state for inspection
        FinalPools = pools;

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
                Balances = BuildBalanceList(startBalances, startBoundary)
            };

            var endDate = endBoundary.AddDays(-1); // 5 April
            var endSnapshot = new BalanceSnapshot
            {
                Label = $"End of {taxYearLabel}",
                Date = endDate,
                Balances = BuildBalanceList(endBalances, endDate)
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
                    Balances = BuildBalanceList(startBalances, lastBoundary)
                };

                // End = current balances (latest data point)
                var endSnapshot = new BalanceSnapshot
                {
                    Label = $"End of {lastLabel} (latest data)",
                    Date = latest,
                    Balances = BuildBalanceList(balances, latest)
                };

                result[lastLabel] = (startSnapshot, endSnapshot);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a list of AssetBalance from raw balances, calling GetGbpValueOfAsset only once per asset.
    /// </summary>
    private List<AssetBalance> BuildBalanceList(Dictionary<string, decimal> rawBalances, DateTimeOffset date)
    {
        return rawBalances
            .Where(kv => kv.Value > 0.00000001m)
            .Select(kv =>
            {
                var gbp = _fxService.GetGbpValueOfAsset(kv.Key, kv.Value, date);
                return new AssetBalance { Asset = kv.Key, Quantity = kv.Value, GbpValue = gbp };
            })
            .OrderByDescending(ab => ab.GbpValue)
            .ToList();
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

        // Always include the current tax year so its tab appears even with no
        // transactions yet (e.g. when a new UK tax year starts on 6 April).
        // Only add it when the user has some data loaded to avoid a lone empty tab.
        if (allTaxYears.Count > 0)
        {
            // Use UK local time so the boundary aligns with 6 April in the UK
            // (UTC can lag behind BST by one hour, causing the wrong tax year near midnight).
            var ukNow = _nowOverride ?? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "GMT Standard Time");
            allTaxYears.Add(GetTaxYearLabel(ukNow));
        }

        var summaries = new List<TaxYearSummary>(allTaxYears.Count);
        decimal carriedLosses = 0; // Running total of unused losses from prior years

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
                // Use net amount (gross - Kraken commission) to match the acquisition event
                // and the amount that actually lands in the user's account.
                var netAmount = s.Amount - s.Fee;
                if (netAmount <= 0) continue;
                var gbpValue = _fxService.GetGbpValueOfAsset(s.NormalisedAsset, netAmount, s.DateTime);
                stakingIncome += gbpValue;
                stakingDetails.Add(new StakingReward
                {
                    Date = s.DateTime,
                    Asset = s.NormalisedAsset,
                    Amount = netAmount,
                    GbpValue = gbpValue
                });
            }

            // Loss carry-forward logic
            var lossesCarriedIn = carriedLosses;
            decimal lossesUsed = 0;

            var otherGains = userInput.OtherCapitalGains;
            var totalNetGain = netGain + otherGains;

            // Current-year losses are already netted in totalNetGain.
            // Carried-in losses are only used to reduce net gains to AEA level (HMRC rule:
            // brought-forward losses reduce gains to the AEA but not below it).
            if (totalNetGain > rates.AnnualExemptAmount && lossesCarriedIn > 0)
            {
                var excessAboveAea = totalNetGain - rates.AnnualExemptAmount;
                lossesUsed = Math.Min(lossesCarriedIn, excessAboveAea);
                totalNetGain -= lossesUsed;
            }

            var taxableGain = Math.Max(0, totalNetGain - rates.AnnualExemptAmount);
            var taxableIncome = userInput.TaxableIncome;
            var cgtDue = CalculateCgt(taxableGain, taxableIncome, rates);

            // Update carried losses: subtract used, add any new current-year net loss
            carriedLosses -= lossesUsed;
            if (netGain + otherGains < 0)
                carriedLosses += Math.Abs(netGain + otherGains); // new losses added to pool

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
                LossesCarriedIn = lossesCarriedIn,
                LossesUsedThisYear = lossesUsed,
                LossesCarriedOut = carriedLosses,
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
        public string Type { get; set; } = "";
    }

    private class AcqRemaining
    {
        public CgtEvent Event { get; set; } = null!;
        public decimal RemainingQty { get; set; }
    }
}
