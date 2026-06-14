using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration.Validation;

/// <summary>
/// Validation attribute to specify minimum TTL value.
/// </summary>
public class MinimumTtlAttribute : ValidationAttribute
{
    private readonly TimeSpan _minimum;

    /// <summary>
    /// Initializes a new instance of the <see cref="MinimumTtlAttribute"/> class.
    /// </summary>
    /// <param name="seconds">The minimum allowed TTL, in seconds.</param>
    public MinimumTtlAttribute(double seconds)
    {
        _minimum = TimeSpan.FromSeconds(seconds);
    }

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        if (value is not TimeSpan ttl)
            return false;

        return ttl >= _minimum;
    }

    /// <inheritdoc />
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

    /// <summary>
    /// Initializes a new instance of the <see cref="MaximumTtlAttribute"/> class.
    /// </summary>
    /// <param name="hours">The maximum allowed TTL, in hours.</param>
    public MaximumTtlAttribute(double hours)
    {
        _maximum = TimeSpan.FromHours(hours);
    }

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        if (value is not TimeSpan ttl)
            return false;

        return ttl <= _maximum;
    }

    /// <inheritdoc />
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
    /// <summary>
    /// Gets the configuration path associated with the validated member.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationPathAttribute"/> class.
    /// </summary>
    /// <param name="path">The configuration path associated with the validated member.</param>
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
    /// <summary>
    /// Gets the suggested remediation text for the validation error.
    /// </summary>
    public string Fix { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestedFixAttribute"/> class.
    /// </summary>
    /// <param name="fix">The suggested remediation text for the validation error.</param>
    public SuggestedFixAttribute(string fix)
    {
        Fix = fix;
    }
}
