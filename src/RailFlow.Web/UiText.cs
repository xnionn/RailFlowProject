using System.Globalization;
using RailFlow.Core.Domain;

namespace RailFlow.Web;

/// <summary>
/// Small pure helpers that turn domain values into the text shown on screen, so every page writes
/// counts, station names, durations and money the same way.
/// </summary>
public static class UiText
{
    /// <summary>"1 passenger", "3 passengers", "1,250 segments".</summary>
    public static string Count(int count, string singular, string? plural = null) =>
        $"{count:N0} {(count == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>Station codes are stored in capitals (LONDON); people read them as names (London).</summary>
    public static string Station(string code) => StationNames.Display(code);

    /// <summary>"Fri 14 Nov 2026".</summary>
    public static string Day(DateOnly day) => day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"45 min", "2 h 05 min".</summary>
    public static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";

    public static string Money(decimal amount, string currency = "€") =>
        amount < 0 ? $"−{currency}{-amount:N2}" : $"{currency}{amount:N2}";

    /// <summary>A fare multiplier written as a percentage change: 1.10 → "+10%".</summary>
    public static string PercentChange(double multiplier) =>
        $"{(multiplier >= 1 ? "+" : "−")}{Math.Abs(multiplier - 1) * 100:0}%";
}
