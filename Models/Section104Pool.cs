namespace CryptoTax2026.Models;

public class Section104Pool
{
    public string Asset { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal PooledCost { get; set; } // total allowable cost in GBP

    public decimal CostPerUnit => Quantity > 0 ? PooledCost / Quantity : 0;

    public void AddTokens(decimal quantity, decimal cost)
    {
        Quantity += quantity;
        PooledCost += cost;
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
