using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Base validation attribute for configuration values that need environment-aware validation.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public abstract class ConfigurationValidationAttribute : ValidationAttribute
{
    /// <summary>
    /// Gets or sets the configuration path used in validation messages.
    /// </summary>
    public string? ConfigurationPath { get; set; }

    /// <summary>
    /// Gets or sets an optional suggested fix for the configuration error.
    /// </summary>
    public string? SuggestedFix { get; set; }

    /// <summary>
    /// Gets a value indicating whether validation is running in development mode.
    /// </summary>
    protected static bool IsDevelopment(ValidationContext validationContext)
        => validationContext.Items.TryGetValue("IsDevelopment", out var value) && value is true;
}

/// <summary>
/// Validates that a configuration value is present.
/// </summary>
public sealed class RequiredConfigurationAttribute : ConfigurationValidationAttribute
{
    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is string stringValue)
        {
            return string.IsNullOrWhiteSpace(stringValue)
                ? new ValidationResult($"{validationContext.DisplayName} is required.")
                : ValidationResult.Success;
        }

        return value is null
            ? new ValidationResult($"{validationContext.DisplayName} is required.")
            : ValidationResult.Success;
    }
}

/// <summary>
/// Validates a configuration URL.
/// </summary>
public sealed class ValidUrlAttribute : ConfigurationValidationAttribute
{
    /// <summary>
    /// Gets or sets the allowed URL schemes.
    /// </summary>
    public string[]? RequiredSchemes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether HTTPS is required outside development.
    /// </summary>
    public bool RequireHttpsInProduction { get; set; }

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
        {
            return ValidationResult.Success;
        }

        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return ValidationResult.Success;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new ValidationResult($"{validationContext.DisplayName} must be a valid absolute URL.");
        }

        if (RequiredSchemes is { Length: > 0 } &&
            !RequiredSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return new ValidationResult(
                $"{validationContext.DisplayName} must use one of: {string.Join(", ", RequiredSchemes)}.");
        }

        if (RequireHttpsInProduction && !IsDevelopment(validationContext) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationResult($"{validationContext.DisplayName} must use HTTPS.");
        }

        return ValidationResult.Success;
    }
}

/// <summary>
/// Validates secret reference formats.
/// </summary>
public sealed class SecretReferenceAttribute : ConfigurationValidationAttribute
{
    /// <summary>
    /// Gets or sets the allowed secret providers.
    /// </summary>
    public string[]? AllowedProviders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether plain text is allowed in development.
    /// </summary>
    public bool AllowPlainTextInDevelopment { get; set; }

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
        {
            return ValidationResult.Success;
        }

        if (value is not string secretValue || string.IsNullOrWhiteSpace(secretValue))
        {
            return ValidationResult.Success;
        }

        var colonIndex = secretValue.IndexOf(':');
        if (colonIndex <= 0 || colonIndex == secretValue.Length - 1)
        {
            return AllowPlainTextInDevelopment && IsDevelopment(validationContext)
                ? ValidationResult.Success
                : new ValidationResult($"{validationContext.DisplayName} must be a valid secret reference.");
        }

        var provider = secretValue[..colonIndex];
        if (AllowedProviders is { Length: > 0 } &&
            !AllowedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return new ValidationResult(
                $"{validationContext.DisplayName} must use one of the allowed providers: {string.Join(", ", AllowedProviders)}.");
        }

        return ValidationResult.Success;
    }
}
