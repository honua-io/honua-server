using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public class ValidTtlAttribute : ValidationAttribute
{
    public ValidTtlAttribute(double minSeconds = 1, double maxHours = 24)
    {
        MinimumTtl = TimeSpan.FromSeconds(minSeconds);
        MaximumTtl = TimeSpan.FromHours(maxHours);
    }

    public TimeSpan MinimumTtl { get; set; }

    public TimeSpan MaximumTtl { get; set; }

    public TimeSpan? RecommendedMinimumInProduction { get; set; }

    public bool WarnInDevelopment { get; set; }

    public override bool IsValid(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (!TryGetTtl(value, out var ttl))
        {
            return false;
        }

        return ttl >= MinimumTtl && ttl <= MaximumTtl;
    }

    public override string FormatErrorMessage(string name)
    {
        return $"{name} must be between {MinimumTtl.TotalSeconds} seconds and {MaximumTtl.TotalHours} hours.";
    }

    private static bool TryGetTtl(object value, out TimeSpan ttl)
    {
        switch (value)
        {
            case TimeSpan timeSpan:
                ttl = timeSpan;
                return true;
            case int seconds when seconds >= 0:
                ttl = TimeSpan.FromSeconds(seconds);
                return true;
            case long seconds when seconds >= 0:
                ttl = TimeSpan.FromSeconds(seconds);
                return true;
            case double seconds when seconds >= 0:
                ttl = TimeSpan.FromSeconds(seconds);
                return true;
            case string timeSpanText when TimeSpan.TryParse(timeSpanText, out var parsed):
                ttl = parsed;
                return true;
            default:
                ttl = default;
                return false;
        }
    }
}
