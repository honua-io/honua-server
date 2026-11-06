using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Core.Configuration;

public abstract class OptionsValidator<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T> : IValidateOptions<T>
    where T : class
{
    protected virtual void ValidateOptions(T options, List<string> failures)
    {
    }

    protected static void ValidateDataAnnotations<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TValue>(
        TValue? value,
        List<string> failures,
        string propertyName)
        where TValue : class
    {
        if (value is null)
        {
            return;
        }

        var validationResults = new List<ValidationResult>();
        var validationContext = CreateValidationContext(value, propertyName);

        if (!TryValidateObject(value, validationContext, validationResults))
        {
            foreach (var result in validationResults)
            {
                failures.Add($"{propertyName}.{result.ErrorMessage}");
            }
        }
    }

    protected static void ValidateOutboundHttpUrl(string? url, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            failures.Add($"{propertyName} is required and cannot be empty.");
            return;
        }

        var result = OutboundHttpUrlValidator.ValidateConfiguration(url);
        if (!result.IsValid)
        {
            failures.Add($"{propertyName} {result.ErrorMessage}");
        }
    }

    protected static void ValidateFileSize(long value, long min, long max, string propertyName, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{propertyName} must be between {FormatByteCount(min)} and {FormatByteCount(max)}.");
        }
    }

    protected static void ValidateLogicalOrder<TValue>(TValue smaller, TValue larger, string smallerName, string largerName, List<string> failures)
        where TValue : IComparable<TValue>
    {
        if (smaller.CompareTo(larger) > 0)
        {
            failures.Add($"{smallerName} must not exceed {largerName}.");
        }
    }

    public ValidateOptionsResult Validate(string? name, T options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validationResults = new List<ValidationResult>();
        var validationContext = CreateValidationContext(options, typeof(T).Name);

        TryValidateObject(options, validationContext, validationResults);
        var errors = validationResults.Select(r => r.ErrorMessage ?? "Validation error").ToList();
        ValidateOptions(options, errors);

        if (errors.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(errors);
    }

    private static string FormatByteCount(long value)
    {
        const long kilobyte = 1024;
        const long megabyte = 1024 * kilobyte;
        const long gigabyte = 1024 * megabyte;

        if (value % gigabyte == 0)
        {
            return $"{value / gigabyte}GB";
        }

        if (value % megabyte == 0)
        {
            return $"{value / megabyte}MB";
        }

        if (value % kilobyte == 0)
        {
            return $"{value / kilobyte}KB";
        }

        return $"{value} bytes";
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "DisplayName is assigned explicitly to avoid trim-unsafe display-name reflection during validation.")]
    private static ValidationContext CreateValidationContext(object instance, string displayName)
    {
        var validationContext = new ValidationContext(instance, serviceProvider: null, items: null)
        {
            DisplayName = displayName
        };

        return validationContext;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Validation context is created with an explicit display name, avoiding trim-unsafe display-name reflection.")]
    private static bool TryValidateObject(
        object instance,
        ValidationContext validationContext,
        ICollection<ValidationResult> validationResults)
    {
        return Validator.TryValidateObject(instance, validationContext, validationResults, validateAllProperties: true);
    }
}
