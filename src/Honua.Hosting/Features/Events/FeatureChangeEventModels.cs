// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Stored feature-change event envelope used for replay and webhook delivery.
/// </summary>
internal sealed record FeatureChangeEvent
{
    public required string EventId { get; init; }
    public required long Cursor { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? SourceId { get; init; }
    public required string ServiceId { get; init; }
    public required int LayerId { get; init; }
    public required long ObjectId { get; init; }
    public required string Operation { get; init; }
    public required string Protocol { get; init; }
    public required string RequestId { get; init; }

    /// <summary>
    /// Optional. Changed attribute values when available. Null when the originating
    /// protocol does not provide attribute-level change tracking, or for deletes.
    /// </summary>
    public Dictionary<string, object?>? ChangedAttributes { get; init; }

    /// <summary>
    /// Whether the feature's geometry was modified by this operation.
    /// Best-effort: may default to false when the originating protocol cannot determine this.
    /// </summary>
    public bool GeometryChanged { get; init; }

    /// <summary>
    /// Geometry bounding box (MinX, MinY, MaxX, MaxY) captured at mutation time.
    /// Null for deletes or non-spatial features.
    /// </summary>
    public double[]? GeometryEnvelope { get; init; }

    /// <summary>
    /// Pre-serialized JSON of feature attributes at mutation time.
    /// Null for deletes.
    /// </summary>
    public string? PropertiesJson { get; init; }

    /// <summary>
    /// Pre-serialized GeoJSON geometry captured at mutation time.
    /// Null for deletes or non-spatial features.
    /// </summary>
    public string? GeometryJson { get; init; }

    /// <summary>
    /// SRID for <see cref="GeometryJson"/> when known.
    /// </summary>
    public int? GeometrySrid { get; init; }
}

/// <summary>
/// Input payload used by writers to publish feature-change events.
/// </summary>
internal sealed record FeatureChangeEventRequest
{
    public string? EventId { get; init; }
    public string? SourceId { get; init; }
    public required string ServiceId { get; init; }
    public required int LayerId { get; init; }
    public required long ObjectId { get; init; }
    public required string Operation { get; init; }
    public required string Protocol { get; init; }
    public required string RequestId { get; init; }
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Optional. Changed attribute values when available. Null when the originating
    /// protocol does not provide attribute-level change tracking, or for deletes.
    /// </summary>
    public Dictionary<string, object?>? ChangedAttributes { get; init; }

    /// <summary>
    /// Whether the feature's geometry was modified by this operation.
    /// Best-effort: may default to false when the originating protocol cannot determine this.
    /// </summary>
    public bool GeometryChanged { get; init; }

    /// <summary>
    /// Geometry bounding box (MinX, MinY, MaxX, MaxY) captured at mutation time.
    /// Null for deletes or non-spatial features.
    /// </summary>
    public double[]? GeometryEnvelope { get; init; }

    /// <summary>
    /// Pre-serialized JSON of feature attributes at mutation time.
    /// Null for deletes.
    /// </summary>
    public string? PropertiesJson { get; init; }

    /// <summary>
    /// Pre-serialized GeoJSON geometry captured at mutation time.
    /// Null for deletes or non-spatial features.
    /// </summary>
    public string? GeometryJson { get; init; }

    /// <summary>
    /// SRID for <see cref="GeometryJson"/> when known.
    /// </summary>
    public int? GeometrySrid { get; init; }
}

/// <summary>
/// Feature-change event configuration.
/// </summary>
public sealed class FeatureChangeEventOptions
{
    public const string SectionName = "FeatureChangeEvents";

    /// <summary>
    /// Maximum number of events retained in-memory for replay.
    /// </summary>
    public int MaxRetainedEvents { get; set; } = 20_000;
}

/// <summary>
/// Webhook delivery configuration for feature-change events.
/// </summary>
public sealed class FeatureChangeWebhookOptions
{
    public const string SectionName = "FeatureChangeEvents:Webhook";

    /// <summary>
    /// Enables outbound webhook delivery.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute webhook URL.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Shared HMAC secret for signature generation.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Maximum delivery attempts per event.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Base retry delay in milliseconds. Exponential backoff doubles this value per attempt.
    /// </summary>
    public int InitialBackoffMs { get; set; } = 500;

    /// <summary>
    /// Upper bound for retry delay in milliseconds.
    /// </summary>
    public int MaxBackoffMs { get; set; } = 30_000;

    /// <summary>
    /// Per-request timeout for webhook calls in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
}

internal readonly record struct FeatureChangeWebhookUrlValidationResult(bool IsValid, Uri? Uri, string? ErrorMessage)
{
    public static FeatureChangeWebhookUrlValidationResult Success(Uri uri)
        => new(true, uri, null);

    public static FeatureChangeWebhookUrlValidationResult Failure(string message)
        => new(false, null, message);
}

internal static class FeatureChangeWebhookUrlValidation
{
    internal const string InvalidHttpsUrlMessage = "Webhook URL must be a valid HTTPS URL.";
    internal const string EmbeddedCredentialsMessage = "Webhook URL must not include embedded credentials.";
    internal const string DisallowedAddressMessage =
        "Webhook URL resolves to a private, loopback, or unresolvable network address, which is not allowed.";

    public static Task<FeatureChangeWebhookUrlValidationResult> ValidateAsync(
        string webhookUrl,
        CancellationToken cancellationToken = default)
        => ValidateAsync(webhookUrl, ResolveHostAddressesAsync, cancellationToken);

    public static FeatureChangeWebhookUrlValidationResult ValidateConfiguration(string webhookUrl)
    {
        if (!TryValidateBaseUri(webhookUrl, out var failure, out var uri))
        {
            return failure;
        }

        if (IPAddress.TryParse(uri.Host, out var literalAddress) && IsPrivateOrReservedAddress(literalAddress))
        {
            return FeatureChangeWebhookUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return FeatureChangeWebhookUrlValidationResult.Success(uri);
    }

    internal static async Task<FeatureChangeWebhookUrlValidationResult> ValidateAsync(
        string webhookUrl,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateBaseUri(webhookUrl, out var failure, out var uri))
        {
            return failure;
        }

        if (await IsPrivateOrUnresolvableAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return FeatureChangeWebhookUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return FeatureChangeWebhookUrlValidationResult.Success(uri);
    }

    private static bool TryValidateBaseUri(
        string webhookUrl,
        out FeatureChangeWebhookUrlValidationResult failure,
        out Uri uri)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out uri!) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            failure = FeatureChangeWebhookUrlValidationResult.Failure(InvalidHttpsUrlMessage);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            failure = FeatureChangeWebhookUrlValidationResult.Failure(EmbeddedCredentialsMessage);
            return false;
        }

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            failure = FeatureChangeWebhookUrlValidationResult.Failure(DisallowedAddressMessage);
            return false;
        }

        failure = default;
        return true;
    }

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
}

internal sealed class FeatureChangeWebhookOptionsValidator : IValidateOptions<FeatureChangeWebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, FeatureChangeWebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            failures.Add("Feature change webhook URL must be configured when webhook delivery is enabled.");
        }
        else
        {
            var validation = FeatureChangeWebhookUrlValidation.ValidateConfiguration(options.Url);
            if (!validation.IsValid)
            {
                failures.Add(validation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
            }
        }

        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            failures.Add("Feature change webhook secret must be configured when webhook delivery is enabled.");
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add("Feature change webhook MaxAttempts must be at least 1.");
        }

        if (options.RequestTimeoutSeconds < 1)
        {
            failures.Add("Feature change webhook RequestTimeoutSeconds must be at least 1 second.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
