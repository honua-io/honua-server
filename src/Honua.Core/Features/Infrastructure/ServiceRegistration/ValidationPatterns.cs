// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.Infrastructure.ServiceRegistration;

/// <summary>
/// Base class for configuration validators with common patterns.
/// </summary>
public abstract class ConfigurationValidator<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        // Perform common validation
        PerformCommonValidation(options, errors);

        // Perform feature-specific validation
        PerformFeatureSpecificValidation(options, errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// Perform validation common across all features.
    /// Override to customize common validation.
    /// </summary>
    protected virtual void PerformCommonValidation(TOptions options, List<string> errors)
    {
        // Validate using data annotations
        var validationResults = new List<ValidationResult>();
        var validationContext = CreateValidationContext(options, typeof(TOptions).Name);

        if (!TryValidateObject(options, validationContext, validationResults))
        {
            errors.AddRange(validationResults.Select(r => r.ErrorMessage ?? "Unknown validation error"));
        }
    }

    /// <summary>
    /// Perform feature-specific validation. Must be implemented by derived classes.
    /// </summary>
    protected abstract void PerformFeatureSpecificValidation(TOptions options, List<string> errors);

    /// <summary>
    /// Helper to validate that a string property is not null or whitespace.
    /// </summary>
    protected static void ValidateRequired(string? value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{propertyName} is required and cannot be empty or whitespace.");
        }
    }

    /// <summary>
    /// Helper to validate that a numeric property is within a range.
    /// </summary>
    protected static void ValidateRange<T>(T value, T min, T max, string propertyName, List<string> errors)
        where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
        {
            errors.Add($"{propertyName} must be between {min} and {max}, but was {value}.");
        }
    }

    /// <summary>
    /// Helper to validate that a collection is not null or empty.
    /// </summary>
    protected static void ValidateCollectionNotEmpty<T>(IEnumerable<T>? collection, string propertyName, List<string> errors)
    {
        if (collection == null || !collection.Any())
        {
            errors.Add($"{propertyName} cannot be null or empty.");
        }
    }

    /// <summary>
    /// Helper to validate that a URI is valid.
    /// </summary>
    protected static void ValidateUri(string? uriString, string propertyName, List<string> errors, UriKind uriKind = UriKind.Absolute)
    {
        if (!string.IsNullOrWhiteSpace(uriString) && !Uri.TryCreate(uriString, uriKind, out _))
        {
            errors.Add($"{propertyName} must be a valid URI, but was '{uriString}'.");
        }
    }

    /// <summary>
    /// Helper to validate that a file path exists.
    /// </summary>
    protected static void ValidateFilePath(string? filePath, string propertyName, List<string> errors, bool mustExist = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors.Add($"{propertyName} is required and cannot be empty.");
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (mustExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                errors.Add($"{propertyName} path '{filePath}' does not exist.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{propertyName} path '{filePath}' is invalid: {ex.Message}");
        }
    }

    /// <summary>
    /// Helper to validate database connection string format.
    /// </summary>
    protected static void ValidateConnectionString(string? connectionString, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add($"{propertyName} is required and cannot be empty.");
            return;
        }

        // Basic validation - must contain key-value pairs separated by semicolons
        if (!connectionString.Contains('=') && !connectionString.Contains(';'))
        {
            errors.Add($"{propertyName} does not appear to be a valid connection string format.");
        }
    }

    /// <summary>
    /// Helper to validate that a TimeSpan is within a range.
    /// </summary>
    protected static void ValidateTimeSpan(TimeSpan value, TimeSpan min, TimeSpan max, string propertyName, List<string> errors)
    {
        if (value < min || value > max)
        {
            errors.Add($"{propertyName} must be between {min.TotalSeconds} seconds and {max.TotalHours} hours, but was {value.TotalSeconds} seconds.");
        }
    }

    /// <summary>
    /// Helper to validate outbound HTTP URLs.
    /// </summary>
    protected static void ValidateOutboundHttpUrl(string? url, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            errors.Add($"{propertyName} is required and cannot be empty.");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            errors.Add($"{propertyName} must be a valid absolute URL, but was '{url}'.");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{propertyName} must use HTTPS scheme, but was '{uri.Scheme}'.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            errors.Add($"{propertyName} must not include embedded credentials.");
            return;
        }

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            errors.Add($"{propertyName} must not target a private or loopback address.");
            return;
        }

        if (IPAddress.TryParse(uri.Host, out var literalAddress) && IsPrivateOrReservedAddress(literalAddress))
        {
            errors.Add($"{propertyName} must not target a private or loopback address.");
        }
    }

    /// <summary>
    /// Helper to validate data annotations on nested objects.
    /// </summary>
    protected static void ValidateDataAnnotations<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TObject>(
        TObject? obj,
        List<string> errors,
        string propertyName)
        where TObject : class
    {
        if (obj == null)
            return;

        var validationResults = new List<ValidationResult>();
        var validationContext = CreateValidationContext(obj, propertyName);

        if (!TryValidateObject(obj, validationContext, validationResults))
        {
            foreach (var result in validationResults)
            {
                errors.Add($"{propertyName}.{result.ErrorMessage}");
            }
        }
    }

    /// <summary>
    /// Helper to validate logical order between two values.
    /// </summary>
    protected static void ValidateLogicalOrder<T>(T smaller, T larger, string smallerName, string largerName, List<string> errors)
        where T : IComparable<T>
    {
        if (smaller.CompareTo(larger) > 0)
        {
            errors.Add($"{smallerName} ({smaller}) should be less than or equal to {largerName} ({larger}).");
        }
    }

    /// <summary>
    /// Helper to validate that a string is not null or whitespace.
    /// </summary>
    protected static void ValidateRequiredString(string? value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{propertyName} is required and cannot be empty or whitespace.");
        }
    }

    /// <summary>
    /// Helper to validate string length.
    /// </summary>
    protected static void ValidateStringLength(string? value, int maxLength, string propertyName, List<string> errors, int minLength = 0)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (value.Length < minLength || value.Length > maxLength)
        {
            errors.Add($"{propertyName} length must be between {minLength} and {maxLength} characters, but was {value.Length}.");
        }
    }

    /// <summary>
    /// Helper to validate collection item counts.
    /// </summary>
    protected static void ValidateCollectionCount<T>(
        IEnumerable<T>? collection,
        int minCount,
        int maxCount,
        string propertyName,
        List<string> errors)
    {
        if (collection == null)
        {
            if (minCount > 0)
            {
                errors.Add($"{propertyName} must contain between {minCount} and {maxCount} items.");
            }

            return;
        }

        var count = collection switch
        {
            ICollection<T> typedCollection => typedCollection.Count,
            System.Collections.ICollection untypedCollection => untypedCollection.Count,
            _ => collection.Count()
        };

        if (count < minCount || count > maxCount)
        {
            errors.Add($"{propertyName} must contain between {minCount} and {maxCount} items, but contained {count}.");
        }
    }

    /// <summary>
    /// Helper to validate GUID strings.
    /// </summary>
    protected static void ValidateGuid(string? value, string propertyName, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Guid.TryParse(value, out _))
        {
            errors.Add($"{propertyName} must be a valid GUID.");
        }
    }

    /// <summary>
    /// Helper to validate file size within acceptable bounds.
    /// </summary>
    protected static void ValidateFileSize(long value, long min, long max, string propertyName, List<string> errors)
    {
        if (value < min || value > max)
        {
            var minMb = min / (1024.0 * 1024.0);
            var maxGb = max / (1024.0 * 1024.0 * 1024.0);
            var valueMb = value / (1024.0 * 1024.0);
            errors.Add($"{propertyName} must be between {minMb:F1}MB and {maxGb:F1}GB, but was {valueMb:F1}MB.");
        }
    }

    /// <summary>
    /// Helper to validate URL format.
    /// </summary>
    protected static void ValidateUrl(string? url, string propertyName, List<string> errors, bool requireHttps = true)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            errors.Add($"{propertyName} must be a valid absolute URL, but was '{url}'.");
            return;
        }

        if (requireHttps && uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{propertyName} must use HTTPS scheme, but was '{uri.Scheme}'.");
        }
        else if (!requireHttps && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            errors.Add($"{propertyName} must use HTTP or HTTPS scheme, but was '{uri.Scheme}'.");
        }
    }

    /// <summary>
    /// Helper to validate file/directory paths.
    /// </summary>
    protected static void ValidatePath(string? path, string propertyName, List<string> errors, bool requireAbsolute = false, bool preventTraversal = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (requireAbsolute && !Path.IsPathRooted(path))
            {
                errors.Add($"{propertyName} must be an absolute path, but was '{path}'.");
                return;
            }

            if (preventTraversal && (path.Contains("..") || path.Contains('~')))
            {
                errors.Add($"{propertyName} cannot contain path traversal sequences (.., ~), but was '{path}'.");
                return;
            }

            // Check for invalid characters
            var invalidChars = Path.GetInvalidPathChars();
            if (path.IndexOfAny(invalidChars) >= 0)
            {
                errors.Add($"{propertyName} contains invalid path characters: '{path}'.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{propertyName} is not a valid path: {ex.Message}");
        }
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

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "DisplayName is assigned explicitly to avoid trim-unsafe display-name reflection during validation.")]
    private static ValidationContext CreateValidationContext(object instance, string displayName)
    {
        var validationContext = new ValidationContext(instance)
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
