using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Validation attribute for TTL (Time To Live) values.
/// </summary>
public class ValidTtlAttribute : ValidationAttribute
{
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;

    public ValidTtlAttribute(double minSeconds = 1, double maxHours = 24)
    {
        _minimum = TimeSpan.FromSeconds(minSeconds);
        _maximum = TimeSpan.FromHours(maxHours);
    }

    public override bool IsValid(object? value)
    {
        if (value is not TimeSpan ttl)
            return false;

        return ttl >= _minimum && ttl <= _maximum;
    }

    public override string FormatErrorMessage(string name)
    {
        return $"{name} must be between {_minimum.TotalSeconds} seconds and {_maximum.TotalHours} hours.";
    }
}