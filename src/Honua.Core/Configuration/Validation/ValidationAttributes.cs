using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration.Validation;

/// <summary>
/// Validation attribute to specify minimum TTL value.
/// </summary>
public class MinimumTtlAttribute : ValidationAttribute
{
    private readonly TimeSpan _minimum;

    public MinimumTtlAttribute(double seconds)
    {
        _minimum = TimeSpan.FromSeconds(seconds);
    }

    public override bool IsValid(object? value)
    {
        if (value is not TimeSpan ttl)
            return false;

        return ttl >= _minimum;
    }

    public override string FormatErrorMessage(string name)
    {
        return $"{name} must be at least {_minimum.TotalSeconds} seconds.";
    }
}

/// <summary>
/// Validation attribute to specify maximum TTL value.
/// </summary>
public class MaximumTtlAttribute : ValidationAttribute
{
    private readonly TimeSpan _maximum;

    public MaximumTtlAttribute(double hours)
    {
        _maximum = TimeSpan.FromHours(hours);
    }

    public override bool IsValid(object? value)
    {
        if (value is not TimeSpan ttl)
            return false;

        return ttl <= _maximum;
    }

    public override string FormatErrorMessage(string name)
    {
        return $"{name} must be at most {_maximum.TotalHours} hours.";
    }
}

/// <summary>
/// Validation attribute to specify the configuration path for validation errors.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class ConfigurationPathAttribute : Attribute
{
    public string Path { get; }

    public ConfigurationPathAttribute(string path)
    {
        Path = path;
    }
}

/// <summary>
/// Validation attribute to suggest a fix for validation errors.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class SuggestedFixAttribute : Attribute
{
    public string Fix { get; }

    public SuggestedFixAttribute(string fix)
    {
        Fix = fix;
    }
}
