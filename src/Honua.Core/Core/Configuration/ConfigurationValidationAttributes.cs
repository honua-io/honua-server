using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public abstract class ConfigurationValidationAttribute : ValidationAttribute
{
    public string? ConfigurationPath { get; set; }

    public string? SuggestedFix { get; set; }

    protected static bool IsDevelopment(ValidationContext validationContext)
        => validationContext.Items.TryGetValue("IsDevelopment", out var value) && value is true;
}

public sealed class RequiredConfigurationAttribute : ConfigurationValidationAttribute
{
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

public sealed class ValidUrlAttribute : ConfigurationValidationAttribute
{
    public string[]? RequiredSchemes { get; set; }

    public bool RequireHttpsInProduction { get; set; }

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

public sealed class SecretReferenceAttribute : ConfigurationValidationAttribute
{
    public string[]? AllowedProviders { get; set; }

    public bool AllowPlainTextInDevelopment { get; set; }

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
