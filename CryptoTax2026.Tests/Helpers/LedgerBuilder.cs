using System;
using System.Collections.Generic;
using CryptoTax2026.Models;

namespace CryptoTax2026.Tests.Helpers;

/// <summary>
/// Fluent builder for creating test ledger entries.
/// </summary>
public class LedgerBuilder
{
    private readonly List<KrakenLedgerEntry> _entries = new();
    private int _idCounter = 1;

    public LedgerBuilder AddTrade(
        DateTimeOffset date,
        string spentAsset, decimal spentAmount,
        string receivedAsset, decimal receivedAmount,
        decimal spentFee = 0, decimal receivedFee = 0,
        string? refId = null)
    {
        var rid = refId ?? $"TRADE-{_idCounter++}";

        _entries.Add(new KrakenLedgerEntry
        {
            RefId = rid,
            Time = date.ToUnixTimeSeconds(),
            Type = "trade",
            Asset = spentAsset,
            AmountStr = (-Math.Abs(spentAmount)).ToString(),
            FeeStr = spentFee.ToString(),
            LedgerId = $"L-{_idCounter++}",
            NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName(spentAsset)
        });

        _entries.Add(new KrakenLedgerEntry
        {
            RefId = rid,
            Time = date.ToUnixTimeSeconds(),
            Type = "trade",
            Asset = receivedAsset,
            AmountStr = Math.Abs(receivedAmount).ToString(),
            FeeStr = receivedFee.ToString(),
            LedgerId = $"L-{_idCounter++}",
            NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName(receivedAsset)
        });

        return this;
    }

    public LedgerBuilder AddDeposit(DateTimeOffset date, string asset, decimal amount, decimal fee = 0)
    {
        _entries.Add(new KrakenLedgerEntry
        {
            RefId = $"DEP-{_idCounter++}",
            Time = date.ToUnixTimeSeconds(),
            Type = "deposit",
            Asset = asset,
            AmountStr = amount.ToString(),
            FeeStr = fee.ToString(),
            LedgerId = $"L-{_idCounter++}",
            NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName(asset)
        });
        return this;
    }

    public LedgerBuilder AddStaking(DateTimeOffset date, string asset, decimal amount)
    {
        _entries.Add(new KrakenLedgerEntry
        {
            RefId = $"STAKE-{_idCounter++}",
            Time = date.ToUnixTimeSeconds(),
            Type = "staking",
            Asset = asset,
            AmountStr = amount.ToString(),
            FeeStr = "0",
            LedgerId = $"L-{_idCounter++}",
            NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName(asset)
        });
        return this;
    }

    public LedgerBuilder AddDividend(DateTimeOffset date, string asset, decimal amount)
    {
        _entries.Add(new KrakenLedgerEntry
        {
            RefId = $"DIV-{_idCounter++}",
            Time = date.ToUnixTimeSeconds(),
            Type = "dividend",
            Asset = asset,
            AmountStr = amount.ToString(),
            FeeStr = "0",
            LedgerId = $"L-{_idCounter++}",
            NormalisedAsset = KrakenLedgerEntry.NormaliseAssetName(asset)
        });
        return this;
    }

    public List<KrakenLedgerEntry> Build() => new(_entries);
}
