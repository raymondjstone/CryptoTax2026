using System;

namespace CryptoTax2026.Models;

public class CalculationWarning
{
    public WarningLevel Level { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset? Date { get; set; }
    public string? Asset { get; set; }
    public string? LedgerId { get; set; }

    public string DateFormatted => Date?.ToString("dd/MM/yyyy HH:mm") ?? "";
}

public enum WarningLevel
{
    Info,
    Warning,
    Error
}
