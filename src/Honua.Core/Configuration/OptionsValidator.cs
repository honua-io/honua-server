// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Honua.Core.Configuration;

/// <summary>
/// Result returned by outbound HTTP URL validation helpers.
/// </summary>
/// <param name="IsValid">Whether the URL passed validation.</param>
/// <param name="Uri">The validated URI when available.</param>
/// <param name="ErrorMessage">The validation error message when invalid.</param>
public readonly record struct OutboundHttpUrlValidationResult(bool IsValid, Uri? Uri, string? ErrorMessage)
{
    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static OutboundHttpUrlValidationResult Success(Uri uri)
        => new(true, uri, null);

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static OutboundHttpUrlValidationResult Failure(string message)
        => new(false, null, message);
}

/// <summary>
/// Validates outbound HTTP destinations to reduce SSRF exposure.
/// </summary>
public static class OutboundHttpUrlValidator
{
    private const string InvalidHttpsUrlMessage = "must be a valid HTTPS URL.";
    private const string InvalidHttpUrlMessage = "must be a valid HTTP or HTTPS URL.";
    private const string EmbeddedCredentialsMessage = "must not include embedded credentials.";
    private const string DisallowedAddressMessage =
        "must not target localhost, loopback, private, reserved, or unresolvable network addresses.";

    /// <summary>
    /// Validates URL syntax and blocks obvious local/private destinations without performing DNS resolution.
    /// </summary>
    public static OutboundHttpUrlValidationResult ValidateConfiguration(string? url, bool requireHttps = true)
    {
        if (!TryValidateBaseUri(url, requireHttps, out var failure, out var uri))
        {
            return failure;
        }

        if (IPAddress.TryParse(uri.Host, out var literalAddress) && IsPrivateOrReservedAddress(literalAddress))
        {
            return OutboundHttpUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return OutboundHttpUrlValidationResult.Success(uri);
    }

    /// <summary>
    /// Validates a URL and resolves hostnames to reject private or unresolvable destinations.
    /// </summary>
    public static Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string? url,
        bool requireHttps = true,
        CancellationToken cancellationToken = default)
        => ValidateAsync(url, ResolveHostAddressesAsync, requireHttps, cancellationToken);

    internal static async Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string? url,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        bool requireHttps = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateBaseUri(url, requireHttps, out var failure, out var uri))
        {
            return failure;
        }

        if (await IsPrivateOrUnresolvableAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return OutboundHttpUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return OutboundHttpUrlValidationResult.Success(uri);
    }

    /// <summary>
    /// Returns true when the host refers to localhost or a localhost subdomain.
    /// </summary>
    public static bool IsLocalhostHostName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the address is loopback, private, reserved, or otherwise unsuitable for outbound calls.
    /// </summary>
    public static bool IsPrivateOrReservedAddress(IPAddress address)
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

            if (bytes[0] == 0 ||
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
                bytes[0] >= 224)
            {
                return true;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();

            if (address.Equals(IPAddress.IPv6None) ||
                address.Equals(IPAddress.IPv6Loopback) ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0) ||
                (bytes[0] & 0xfe) == 0xfc ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryValidateBaseUri(
        string? url,
        bool requireHttps,
        out OutboundHttpUrlValidationResult failure,
        out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!) ||
            !IsAllowedScheme(uri.Scheme, requireHttps))
        {
            failure = OutboundHttpUrlValidationResult.Failure(requireHttps ? InvalidHttpsUrlMessage : InvalidHttpUrlMessage);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            failure = OutboundHttpUrlValidationResult.Failure(EmbeddedCredentialsMessage);
            return false;
        }

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            failure = OutboundHttpUrlValidationResult.Failure(DisallowedAddressMessage);
            return false;
        }

        failure = default;
        return true;
    }

    private static bool IsAllowedScheme(string scheme, bool requireHttps)
        => requireHttps
            ? string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            : string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
              || string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    private static async Task<bool> IsPrivateOrUnresolvableAddressAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            return IsPrivateOrReservedAddress(literalAddress);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (addresses.Length == 0)
        {
            return true;
        }

        foreach (var address in addresses)
        {
            if (IsPrivateOrReservedAddress(address))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Base class for configuration validators that provides common validation helper methods.
/// Reduces code duplication across configuration validator classes by providing reusable
/// validation patterns for ranges, strings, time spans, file sizes, and more.
/// </summary>
/// <typeparam name="T">The type of options being validated</typeparam>
public abstract class OptionsValidator<T> : IValidateOptions<T> where T : class
{
    /// <summary>
    /// Validates the options configuration.
    /// Derived classes should override this method to perform specific validation logic.
    /// </summary>
    /// <param name="name">The name of the options instance being validated</param>
    /// <param name="options">The options instance to validate</param>
    /// <returns>Validation result with any errors</returns>
    public ValidateOptionsResult Validate(string? name, T options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // Validate individual properties using DataAnnotations first
        ValidateDataAnnotations(options, failures);

        // Call derived class validation logic
        ValidateOptions(options, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates the options configuration using derived class-specific logic.
    /// Override this method to implement custom validation rules.
    /// </summary>
    /// <param name="options">The options instance to validate</param>
    /// <param name="failures">List to add validation errors to</param>
    protected abstract void ValidateOptions(T options, List<string> failures);

    /// <summary>
    /// Validates an object using its DataAnnotations attributes.
    /// </summary>
    /// <param name="obj">The object to validate</param>
    /// <param name="failures">List to add validation errors to</param>
    /// <param name="propertyPath">Optional property path prefix for error messages</param>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification = "DataAnnotations validation is only used at startup for configuration validation")]
    protected static void ValidateDataAnnotations(object obj, List<string> failures, string? propertyPath = null)
    {
        var context = new ValidationContext(obj, serviceProvider: null, items: null);
        if (!string.IsNullOrEmpty(propertyPath))
        {
            context.DisplayName = propertyPath;
        }

        var results = new List<ValidationResult>();

        if (!Validator.TryValidateObject(obj, context, results, true))
        {
            foreach (var result in results)
            {
                var memberName = result.MemberNames.FirstOrDefault() ?? "Unknown";
                var prefix = string.IsNullOrEmpty(propertyPath) ? "" : $"{propertyPath}.";
                failures.Add($"{prefix}{memberName}: {result.ErrorMessage}");
            }
        }
    }

    /// <summary>
    /// Validates that an integer value is within the specified range.
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="min">Minimum allowed value (inclusive)</param>
    /// <param name="max">Maximum allowed value (inclusive)</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    protected static void ValidateRange(int value, int min, int max, string propertyName, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{propertyName} ({value}) must be between {min} and {max}");
        }
    }

    /// <summary>
    /// Validates that a long value is within the specified range.
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="min">Minimum allowed value (inclusive)</param>
    /// <param name="max">Maximum allowed value (inclusive)</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    protected static void ValidateRange(long value, long min, long max, string propertyName, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{propertyName} ({value:N0}) must be between {min:N0} and {max:N0}");
        }
    }

    /// <summary>
    /// Validates that a double value is within the specified range.
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="min">Minimum allowed value (inclusive)</param>
    /// <param name="max">Maximum allowed value (inclusive)</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    protected static void ValidateRange(double value, double min, double max, string propertyName, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{propertyName} ({value}) must be between {min} and {max}");
        }
    }

    /// <summary>
    /// Validates that a file size is within the specified range.
    /// </summary>
    /// <param name="value">The file size in bytes</param>
    /// <param name="minSize">Minimum allowed size in bytes</param>
    /// <param name="maxSize">Maximum allowed size in bytes</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    protected static void ValidateFileSize(long value, long minSize, long maxSize, string propertyName, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{propertyName} must be positive");
        }
        else if (value < minSize || value > maxSize)
        {
            failures.Add($"{propertyName} ({FormatFileSize(value)}) must be between {FormatFileSize(minSize)} and {FormatFileSize(maxSize)}");
        }
    }

    /// <summary>
    /// Validates that a TimeSpan value is within the specified range.
    /// </summary>
    /// <param name="value">The TimeSpan to validate</param>
    /// <param name="min">Minimum allowed TimeSpan</param>
    /// <param name="max">Maximum allowed TimeSpan</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    protected static void ValidateTimeSpan(TimeSpan value, TimeSpan min, TimeSpan max, string propertyName, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{propertyName} must be positive");
        }
        else if (value < min || value > max)
        {
            failures.Add($"{propertyName} must be between {FormatTimeSpan(min)} and {FormatTimeSpan(max)}");
        }
    }

    /// <summary>
    /// Validates that a string is not null, empty, or whitespace-only.
    /// </summary>
    /// <param name="value">The string to validate</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    protected static void ValidateRequiredString(string? value, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{propertyName} cannot be empty");
        }
    }

    /// <summary>
    /// Validates that a string meets length requirements.
    /// </summary>
    /// <param name="value">The string to validate</param>
    /// <param name="maxLength">Maximum allowed length</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    /// <param name="minLength">Optional minimum length</param>
    protected static void ValidateStringLength(string? value, int maxLength, string propertyName, List<string> failures, int minLength = 0)
    {
        if (value == null)
            return;

        if (value.Length < minLength)
        {
            failures.Add($"{propertyName} must be at least {minLength} characters");
        }
        else if (value.Length > maxLength)
        {
            failures.Add($"{propertyName} should not exceed {maxLength} characters");
        }
    }

    /// <summary>
    /// Validates that a URL is a valid absolute URI with HTTPS scheme.
    /// </summary>
    /// <param name="url">The URL to validate</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    /// <param name="requireHttps">Whether to require HTTPS (default: true)</param>
    protected static void ValidateUrl(string? url, string propertyName, List<string> failures, bool requireHttps = true)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            failures.Add($"{propertyName} cannot be empty");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            failures.Add($"{propertyName} must be a valid absolute URL");
            return;
        }

        if (requireHttps && uri.Scheme != "https")
        {
            failures.Add($"{propertyName} must use HTTPS for security");
        }
        else if (!requireHttps && uri.Scheme != "http" && uri.Scheme != "https")
        {
            failures.Add($"{propertyName} must use HTTP or HTTPS scheme");
        }
    }

    /// <summary>
    /// Validates an outbound HTTP destination used for calling external services.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="propertyName">Name of the property being validated.</param>
    /// <param name="failures">List to add validation errors to.</param>
    /// <param name="requireHttps">Whether to require HTTPS (default: true).</param>
    protected static void ValidateOutboundHttpUrl(string? url, string propertyName, List<string> failures, bool requireHttps = true)
    {
        var validation = OutboundHttpUrlValidator.ValidateConfiguration(url, requireHttps);
        if (!validation.IsValid)
        {
            failures.Add($"{propertyName} {validation.ErrorMessage ?? (requireHttps ? "must be a valid HTTPS URL." : "must be a valid HTTP or HTTPS URL.")}");
        }
    }

    /// <summary>
    /// Validates that an HTTP header name is syntactically safe and not on a deny list.
    /// </summary>
    protected static void ValidateHttpHeaderName(
        string? headerName,
        string propertyName,
        List<string> failures,
        bool required = false,
        ISet<string>? disallowedNames = null)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            if (required)
            {
                failures.Add($"{propertyName} cannot be empty");
            }

            return;
        }

        if (!IsValidHttpHeaderName(headerName))
        {
            failures.Add($"{propertyName} must be a valid HTTP header name");
            return;
        }

        if (disallowedNames != null && disallowedNames.Contains(headerName))
        {
            failures.Add($"{propertyName} header '{headerName}' is not allowed");
        }
    }

    /// <summary>
    /// Validates that an HTTP header value does not contain control characters that can break request framing.
    /// </summary>
    protected static void ValidateHttpHeaderValue(
        string? headerValue,
        string propertyName,
        List<string> failures,
        bool required = false)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            if (required)
            {
                failures.Add($"{propertyName} cannot be empty");
            }

            return;
        }

        if (ContainsInvalidHeaderValueCharacter(headerValue))
        {
            failures.Add($"{propertyName} contains invalid control characters");
        }
    }

    /// <summary>
    /// Validates a file or directory path for security and format.
    /// </summary>
    /// <param name="path">The path to validate</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    /// <param name="requireAbsolute">Whether to require an absolute path (default: false)</param>
    /// <param name="preventTraversal">Whether to prevent directory traversal attempts (default: true)</param>
    protected static void ValidatePath(string? path, string propertyName, List<string> failures, bool requireAbsolute = false, bool preventTraversal = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{propertyName} cannot be empty");
            return;
        }

        // Check for invalid path characters
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            failures.Add($"{propertyName} contains invalid path characters");
        }

        // Security check - prevent directory traversal
        if (preventTraversal && path.Contains(".."))
        {
            failures.Add($"{propertyName} cannot contain '..' to prevent directory traversal attacks");
        }

        // Check if absolute path is required
        if (requireAbsolute && !Path.IsPathRooted(path))
        {
            failures.Add($"{propertyName} should be an absolute path for predictable behavior");
        }

        // Path format validation
        if (path.StartsWith('/'))
        {
            // Unix-style path validations
            if (path.Contains("//"))
            {
                failures.Add($"{propertyName} cannot contain consecutive slashes");
            }
        }
    }

    /// <summary>
    /// Validates that a collection has items within the specified count range.
    /// </summary>
    /// <param name="collection">The collection to validate</param>
    /// <param name="minCount">Minimum number of items required</param>
    /// <param name="maxCount">Maximum number of items allowed</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    protected static void ValidateCollectionCount<TItem>(ICollection<TItem>? collection, int minCount, int maxCount, string propertyName, List<string> failures)
    {
        if (collection == null)
        {
            if (minCount > 0)
            {
                failures.Add($"{propertyName} cannot be null");
            }
            return;
        }

        var count = collection.Count;
        if (count < minCount)
        {
            failures.Add($"{propertyName} must contain at least {minCount} item{(minCount == 1 ? "" : "s")}");
        }
        else if (count > maxCount)
        {
            failures.Add($"{propertyName} should not contain more than {maxCount} item{(maxCount == 1 ? "" : "s")}");
        }
    }

    /// <summary>
    /// Validates that a GUID string is in valid format.
    /// </summary>
    /// <param name="guidString">The GUID string to validate</param>
    /// <param name="propertyName">Name of the property being validated</param>
    /// <param name="failures">List to add validation errors to</param>
    protected static void ValidateGuid(string? guidString, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(guidString))
        {
            failures.Add($"{propertyName} cannot be empty");
        }
        else if (!Guid.TryParse(guidString, out _))
        {
            failures.Add($"{propertyName} must be a valid GUID");
        }
    }

    /// <summary>
    /// Validates that two values follow a logical relationship (e.g., min less than or equal to max).
    /// </summary>
    /// <param name="value1">First value</param>
    /// <param name="value2">Second value</param>
    /// <param name="property1Name">Name of the first property</param>
    /// <param name="property2Name">Name of the second property</param>
    /// <param name="failures">List to add validation errors to</param>
    /// <param name="allowEqual">Whether the values can be equal (default: true)</param>
    protected static void ValidateLogicalOrder<TValue>(TValue value1, TValue value2, string property1Name, string property2Name, List<string> failures, bool allowEqual = true)
        where TValue : IComparable<TValue>
    {
        var comparison = value1.CompareTo(value2);
        if (comparison > 0 || (!allowEqual && comparison == 0))
        {
            var operator1 = allowEqual ? "not exceed" : "be less than";
            failures.Add($"{property1Name} ({value1}) must {operator1} {property2Name} ({value2})");
        }
    }

    #region Helper Methods

    /// <summary>
    /// Formats a file size in bytes to a human-readable string.
    /// </summary>
    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} bytes",
            < 1024 * 1024 => $"{bytes / 1024}KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024)}MB",
            _ => $"{bytes / (1024L * 1024L * 1024L)}GB"
        };
    }

    /// <summary>
    /// Formats a TimeSpan to a human-readable string.
    /// </summary>
    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        return timeSpan.TotalDays >= 1 ? $"{timeSpan.TotalDays:F0} days" :
               timeSpan.TotalHours >= 1 ? $"{timeSpan.TotalHours:F0} hours" :
               timeSpan.TotalMinutes >= 1 ? $"{timeSpan.TotalMinutes:F0} minutes" :
               $"{timeSpan.TotalSeconds:F0} seconds";
    }

    private static bool IsValidHttpHeaderName(string value)
    {
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) ||
                  character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsInvalidHeaderValueCharacter(string value)
    {
        foreach (var character in value)
        {
            if ((character < 0x20 && character != '\t') || character == 0x7f)
            {
                return true;
            }
        }

        return false;
    }

    #endregion
}
