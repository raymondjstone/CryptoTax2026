using System;
using System.Collections.Generic;

namespace CryptoTax2026.Models;

public class PoolHistoryEntry
{
    public DateTimeOffset Date { get; set; }
    public decimal Quantity { get; set; }
    public decimal Cost { get; set; }
    public string RefId { get; set; } = "";
}

public class Section104Pool
{
    public string Asset { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal PooledCost { get; set; } // total allowable cost in GBP

    public decimal CostPerUnit => Quantity > 0 ? PooledCost / Quantity : 0;

    // Ordered list of acquisitions that make up the pool (excludes same-day/B&B matched portions)
    public List<PoolHistoryEntry> History { get; } = new();

    public void AddTokens(decimal quantity, decimal cost, DateTimeOffset date = default, string refId = "")
    {
        Quantity += quantity;
        PooledCost += cost;
        History.Add(new PoolHistoryEntry { Date = date, Quantity = quantity, Cost = cost, RefId = refId });
    }

    public decimal RemoveTokens(decimal quantity)
    {
        if (Quantity <= 0) return 0;

        var proportion = quantity / Quantity;
        if (proportion > 1m) proportion = 1m;

        var costRemoved = PooledCost * proportion;
        Quantity -= quantity;
        if (Quantity < 0) Quantity = 0;
        PooledCost -= costRemoved;
        if (PooledCost < 0) PooledCost = 0;

        return costRemoved;
    }
}
