using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    /// Validates an outbound HTTP or HTTPS URL.
    /// </summary>
    protected static void ValidateOutboundHttpUrl(string? url, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            failures.Add($"{propertyName} is required and cannot be empty.");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            failures.Add($"{propertyName} must be a valid absolute URL.");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{propertyName} must use HTTPS.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            failures.Add($"{propertyName} must not include embedded credentials.");
            return;
        }

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            failures.Add($"{propertyName} must not target a private or loopback address.");
            return;
        }

        if (IPAddress.TryParse(uri.Host, out var literalAddress) && IsPrivateOrReservedAddress(literalAddress))
        {
            failures.Add($"{propertyName} must not target a private or loopback address.");
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

    private static bool IsLocalhostHostName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0 ||
                   bytes[0] == 10 ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) ||
                   (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                   (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
                   bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6None) ||
                   address.Equals(IPAddress.IPv6Loopback) ||
                   (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                   (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0) ||
                   (bytes[0] & 0xfe) == 0xfc ||
                   (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }

        return false;
    }
}
