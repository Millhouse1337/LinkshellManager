namespace LinkshellManagerDiscordApp.Services;

/// <summary>
/// DKP values are snapped to each linkshell's configured rounding increment
/// (<c>Linkshell.DkpRoundingIncrement</c>) so points always land on the same
/// grid — nearest 0.25 ("Quarter", the default) or nearest 0.5 ("Half").
/// Centralized here so the per-hour event award and the Hybrid loot
/// spend/refund round the exact same way instead of each using its own literal.
/// </summary>
public static class DkpRounding
{
    public const double QuarterStep = 0.25;
    public const double HalfStep = 0.5;

    /// <summary>The grid step for a linkshell's <c>DkpRoundingIncrement</c> setting.</summary>
    public static double StepFor(string? increment)
        => string.Equals(increment, "Half", StringComparison.OrdinalIgnoreCase) ? HalfStep : QuarterStep;

    /// <summary>Round <paramref name="value"/> to the nearest multiple of <paramref name="step"/>.</summary>
    public static double Round(double value, double step)
    {
        if (step <= 0)
        {
            return value;
        }

        var multiplier = 1d / step;
        return Math.Round(value * multiplier) / multiplier;
    }
}
