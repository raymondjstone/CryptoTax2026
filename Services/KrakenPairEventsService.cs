using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CryptoTax2026.Models;

namespace CryptoTax2026.Services;

/// <summary>
/// Reads <c>kraken_pairs_events.json</c> (shipped alongside the executable) and exposes the
/// full delist / relist history for every Kraken trading pair that has ever been delisted.
///
/// The file contains only pairs that are currently or were historically delisted; actively-listed
/// pairs that have never been delisted are absent.
///
/// Primary uses:
/// <list type="bullet">
///   <item>Seed the initial <see cref="AppSettings.DelistedAssets"/> list on first run.</item>
///   <item>Check whether a given pair was active at a specific point in time (for FX lookups).</item>
/// </list>
/// </summary>
public class KrakenPairEventsService
{
    // pair altname (upper) → ordered list of (delistDate, relistDate?)
    private readonly Dictionary<string, List<(DateOnly Delist, DateOnly? Relist)>> _periods
        = new(StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────────── Construction ────────────────────────────────

    private KrakenPairEventsService() { }

    /// <summary>
    /// Loads the pair-events database from the JSON file that ships with the application.
    /// Returns <c>null</c> if the file is not found or cannot be parsed.
    /// </summary>
    public static KrakenPairEventsService? TryLoad(string? jsonPath = null)
    {
        var path = jsonPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "kraken_pairs_events.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("pairs", out var pairsEl))
                return null;

            var svc = new KrakenPairEventsService();

            foreach (var pairEl in pairsEl.EnumerateArray())
            {
                var altname = pairEl.TryGetProperty("altname", out var altEl)
                    ? altEl.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(altname))
                    continue;

                if (!pairEl.TryGetProperty("events", out var eventsEl))
                    continue;

                // Walk the ordered event list and pair each "delisted" with the following "relisted"
                var periods = new List<(DateOnly Delist, DateOnly? Relist)>();
                DateOnly? pendingDelist = null;

                foreach (var ev in eventsEl.EnumerateArray())
                {
                    var type = ev.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                    var dateStr = ev.TryGetProperty("date", out var dateEl) ? dateEl.GetString() : null;
                    if (type == null || dateStr == null) continue;
                    if (!DateOnly.TryParse(dateStr, out var date)) continue;

                    if (type.Equals("delisted", StringComparison.OrdinalIgnoreCase))
                    {
                        // Close the previous open period first (back-to-back delists without relist)
                        if (pendingDelist.HasValue)
                            periods.Add((pendingDelist.Value, null));
                        pendingDelist = date;
                    }
                    else if (type.Equals("relisted", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pendingDelist.HasValue)
                        {
                            periods.Add((pendingDelist.Value, date));
                            pendingDelist = null;
                        }
                    }
                }

                // Any trailing open delist (no matching relist) → still delisted
                if (pendingDelist.HasValue)
                    periods.Add((pendingDelist.Value, null));

                if (periods.Count > 0)
                    svc._periods[altname.ToUpperInvariant()] = periods;
            }

            return svc;
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────────── Queries ─────────────────────────────────────

    /// <summary>Returns <c>true</c> when <paramref name="pair"/> was delisted at <paramref name="date"/>.</summary>
    public bool IsPairDelistedAt(string pair, DateOnly date)
    {
        if (!_periods.TryGetValue(pair.ToUpperInvariant(), out var periods))
            return false;

        foreach (var (delist, relist) in periods)
        {
            if (date >= delist && (relist == null || date < relist))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the delist/relist periods for a pair, or an empty list if the pair is not in
    /// the database.
    /// </summary>
    public IReadOnlyList<(DateOnly Delist, DateOnly? Relist)> GetPeriods(string pair)
    {
        return _periods.TryGetValue(pair.ToUpperInvariant(), out var p) ? p : Array.Empty<(DateOnly, DateOnly?)>();
    }

    // ─────────────────────────── Default event list ──────────────────────────────

    /// <summary>
    /// Returns one <see cref="DelistedAssetEvent"/> per tracked pair.  Each entry represents
    /// the <em>most-recent</em> delist period (open-ended if the pair is still delisted).
    /// These events are intended to pre-populate <see cref="AppSettings.DelistedAssets"/>
    /// on first run; they use <c>ClaimType = "Delisted"</c> so they inform the engine about
    /// pair availability without automatically triggering £0 disposal calculations.
    /// </summary>
    // KUSD (Kraken USD stablecoin) was confirmed delisted on 14 July 2025.
    // The JSON snapshot only captures an approximate date, so we hardcode the correct one.
    private static readonly DateTimeOffset KusdDelistDate = new(2025, 7, 14, 0, 0, 0, TimeSpan.Zero);

    public List<DelistedAssetEvent> GetDefaultDelistEvents()
    {
        var result = new List<DelistedAssetEvent>(_periods.Count);
        foreach (var (pair, periods) in _periods)
        {
            if (periods.Count == 0) continue;
            var last = periods[^1];
            var delistDate = pair.Equals("KUSD", StringComparison.OrdinalIgnoreCase)
                ? KusdDelistDate
                : last.Delist.ToDateTimeOffset();
            result.Add(new DelistedAssetEvent
            {
                Pair = pair,
                DelistingDate = delistDate,
                RelistDate = last.Relist?.ToDateTimeOffset(),
                Notes = "Kraken",
                ClaimType = "Delisted"
            });
        }
        return result;
    }

    // ─────────────────────────────── Helpers ─────────────────────────────────────

    public int PairCount => _periods.Count;
}

file static class DateOnlyExtensions
{
    internal static DateTimeOffset ToDateTimeOffset(this DateOnly d)
        => new(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero);
}
