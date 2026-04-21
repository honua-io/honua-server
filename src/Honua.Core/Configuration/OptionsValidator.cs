using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Core.Configuration;

/// <summary>
/// Base class for options validation using data annotations.
/// </summary>
/// <typeparam name="T">The options type to validate</typeparam>
public abstract class OptionsValidator<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T> : IValidateOptions<T>
    where T : class
{
    /// <summary>
    /// Performs custom validation for an options instance after data annotation validation.
    /// </summary>
    /// <param name="options">The options instance to validate.</param>
    /// <param name="failures">The mutable collection of validation failures.</param>
    protected virtual void ValidateOptions(T options, List<string> failures)
    {
    }

    /// <summary>
    /// Validates a nested object using its data annotation attributes.
    /// </summary>
<<<<<<< HEAD
    protected static void ValidateDataAnnotations(object? value, List<string> failures, string propertyName)
=======
    protected static void ValidateDataAnnotations<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TValue>(
        TValue? value,
        List<string> failures,
        string propertyName)
        where TValue : class
>>>>>>> origin/trunk
    {
        if (value is null)
        {
            return;
        }

        var validationResults = new List<ValidationResult>();
<<<<<<< HEAD
        var validationContext = new ValidationContext(value, serviceProvider: null, items: null);

        if (!Validator.TryValidateObject(value, validationContext, validationResults, validateAllProperties: true))
=======
        var validationContext = CreateValidationContext(value, propertyName);

        if (!TryValidateObject(value, validationContext, validationResults))
>>>>>>> origin/trunk
        {
            foreach (var result in validationResults)
            {
                failures.Add($"{propertyName}.{result.ErrorMessage}");
            }
        }
    }

    /// <summary>
<<<<<<< HEAD
    /// Validates an outbound HTTP or HTTPS URL.
=======
    /// Validates an outbound HTTPS URL via <see cref="OutboundHttpUrlValidator.ValidateConfiguration(string)"/>,
    /// which performs the same DNS-aware reservation checks as the runtime path so configuration-time
    /// validation does not silently accept hostnames that the runtime would later reject.
>>>>>>> origin/trunk
    /// </summary>
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
<<<<<<< HEAD
            failures.Add($"{propertyName} must be a valid absolute URL.");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{propertyName} must use HTTP or HTTPS.");
=======
            failures.Add($"{propertyName} {result.ErrorMessage}");
>>>>>>> origin/trunk
        }
    }

    /// <summary>
    /// Validates a file size is within an inclusive range.
    /// </summary>
    protected static void ValidateFileSize(long value, long min, long max, string propertyName, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{propertyName} must be between {FormatByteCount(min)} and {FormatByteCount(max)}.");
        }
    }

    /// <summary>
    /// Validates that a smaller bound does not exceed a larger bound.
    /// </summary>
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
<<<<<<< HEAD
        var validationContext = new ValidationContext(options, serviceProvider: null, items: null);

        Validator.TryValidateObject(options, validationContext, validationResults, validateAllProperties: true);
=======
        var validationContext = CreateValidationContext(options, typeof(T).Name);

        TryValidateObject(options, validationContext, validationResults);
>>>>>>> origin/trunk
        var errors = validationResults.Select(r => r.ErrorMessage ?? "Validation error").ToList();
        ValidateOptions(options, errors);

        if (errors.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(errors);
    }
<<<<<<< HEAD

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
=======

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
>>>>>>> origin/trunk
    }
}
