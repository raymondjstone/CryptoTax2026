using System;
using CryptoTax2026.Models;
using Xunit;

namespace CryptoTax2026.Tests.Models;

public class Section104PoolTests
{
    [Fact]
    public void NewPool_HasZeroQuantityAndCost()
    {
        var pool = new Section104Pool { Asset = "BTC" };
        Assert.Equal(0m, pool.Quantity);
        Assert.Equal(0m, pool.PooledCost);
        Assert.Equal(0m, pool.CostPerUnit);
    }

    [Fact]
    public void AddTokens_IncreasesQuantityAndCost()
    {
        var pool = new Section104Pool { Asset = "BTC" };
        pool.AddTokens(2m, 50000m);
        Assert.Equal(2m, pool.Quantity);
        Assert.Equal(50000m, pool.PooledCost);
    }

    [Fact]
    public void AddTokens_MultiplePurchases_AveragesCost()
    {
        var pool = new Section104Pool { Asset = "ETH" };
        pool.AddTokens(10m, 10000m); // 10 ETH at £1000 each
        pool.AddTokens(10m, 20000m); // 10 ETH at £2000 each

        Assert.Equal(20m, pool.Quantity);
        Assert.Equal(30000m, pool.PooledCost);
        Assert.Equal(1500m, pool.CostPerUnit); // Average: £1500
    }

    [Fact]
    public void RemoveTokens_ReturnsProportionalCost()
    {
        var pool = new Section104Pool { Asset = "ETH" };
        pool.AddTokens(10m, 10000m); // £1000 per ETH

        var costRemoved = pool.RemoveTokens(3m);
        Assert.Equal(3000m, costRemoved); // 3/10 * 10000
        Assert.Equal(7m, pool.Quantity);
        Assert.Equal(7000m, pool.PooledCost);
    }

    [Fact]
    public void RemoveTokens_EntirePool_RemovesAll()
    {
        var pool = new Section104Pool { Asset = "BTC" };
        pool.AddTokens(5m, 100000m);

        var costRemoved = pool.RemoveTokens(5m);
        Assert.Equal(100000m, costRemoved);
        Assert.Equal(0m, pool.Quantity);
        Assert.Equal(0m, pool.PooledCost);
    }

    [Fact]
    public void RemoveTokens_MoreThanAvailable_ClampsToAvailable()
    {
        var pool = new Section104Pool { Asset = "BTC" };
        pool.AddTokens(2m, 40000m);

        var costRemoved = pool.RemoveTokens(5m);
        // proportion = 5/2 = 2.5, clamped to 1.0 => cost = 40000
        Assert.Equal(40000m, costRemoved);
        // Quantity goes negative then clamped to 0
        Assert.Equal(0m, pool.Quantity);
        Assert.Equal(0m, pool.PooledCost);
    }

    [Fact]
    public void RemoveTokens_EmptyPool_ReturnsZero()
    {
        var pool = new Section104Pool { Asset = "BTC" };

        var costRemoved = pool.RemoveTokens(1m);
        Assert.Equal(0m, costRemoved);
    }

    [Fact]
    public void CostPerUnit_CalculatesCorrectly()
    {
        var pool = new Section104Pool { Asset = "ETH" };
        pool.AddTokens(4m, 8000m);
        Assert.Equal(2000m, pool.CostPerUnit);
    }

    [Fact]
    public void CostPerUnit_ZeroQuantity_ReturnsZero()
    {
        var pool = new Section104Pool { Asset = "ETH" };
        Assert.Equal(0m, pool.CostPerUnit);
    }

    [Fact]
    public void AddThenRemove_MaintainsCorrectAverages()
    {
        var pool = new Section104Pool { Asset = "ETH" };

        // Buy 10 at £1000
        pool.AddTokens(10m, 10000m);
        Assert.Equal(1000m, pool.CostPerUnit);

        // Sell 4
        pool.RemoveTokens(4m);
        Assert.Equal(6m, pool.Quantity);
        Assert.Equal(6000m, pool.PooledCost);
        Assert.Equal(1000m, pool.CostPerUnit); // Average unchanged

        // Buy 4 more at £2000
        pool.AddTokens(4m, 8000m);
        Assert.Equal(10m, pool.Quantity);
        Assert.Equal(14000m, pool.PooledCost);
        Assert.Equal(1400m, pool.CostPerUnit); // New average
    }

    // ========== History Tracking ==========

    [Fact]
    public void AddTokens_RecordsHistory()
    {
        var pool = new Section104Pool { Asset = "ETH" };
        var date = new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero);

        pool.AddTokens(5m, 10000m, date, "REF-1", "trade");

        Assert.Single(pool.History);
        Assert.Equal(5m, pool.History[0].Quantity);
        Assert.Equal(10000m, pool.History[0].Cost);
        Assert.Equal("REF-1", pool.History[0].RefId);
        Assert.Equal("trade", pool.History[0].Type);
        Assert.Equal(date, pool.History[0].Date);
    }

    [Fact]
    public void AddTokens_MultipleAdds_HistoryPreservesOrder()
    {
        var pool = new Section104Pool { Asset = "BTC" };

        pool.AddTokens(1m, 30000m, refId: "A");
        pool.AddTokens(2m, 70000m, refId: "B");
        pool.AddTokens(0.5m, 20000m, refId: "C");

        Assert.Equal(3, pool.History.Count);
        Assert.Equal("A", pool.History[0].RefId);
        Assert.Equal("B", pool.History[1].RefId);
        Assert.Equal("C", pool.History[2].RefId);
    }

    [Fact]
    public void RemoveTokens_DoesNotAffectHistory()
    {
        var pool = new Section104Pool { Asset = "ETH" };
        pool.AddTokens(10m, 20000m, refId: "BUY-1");
        pool.AddTokens(5m, 15000m, refId: "BUY-2");

        pool.RemoveTokens(8m);

        // History should still show both acquisitions
        Assert.Equal(2, pool.History.Count);
        // But pool quantities should reflect removal
        Assert.Equal(7m, pool.Quantity);
    }
}
