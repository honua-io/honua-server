// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using System.Net;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

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
    public string? OperationInstanceId { get; init; }
    public string? CorrelationId { get; init; }
    public string? AuditId { get; init; }
    public string? ProposalId { get; init; }

    // Null attributes can mean the protocol has no attribute-level tracking, including deletes. GeometryChanged false does not prove the geometry is unchanged.
    public Dictionary<string, object?>? ChangedAttributes { get; init; }

    public bool GeometryChanged { get; init; }

    public double[]? GeometryEnvelope { get; init; }

    public string? PropertiesJson { get; init; }

    public string? GeometryJson { get; init; }

    public int? GeometrySrid { get; init; }
}

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
    public string? OperationInstanceId { get; init; }
    public string? CorrelationId { get; init; }
    public string? AuditId { get; init; }
    public string? ProposalId { get; init; }
    public DateTimeOffset? Timestamp { get; init; }

    // Null attributes can mean the protocol has no attribute-level tracking, including deletes. GeometryChanged false does not prove the geometry is unchanged.
    public Dictionary<string, object?>? ChangedAttributes { get; init; }

    public bool GeometryChanged { get; init; }

    public double[]? GeometryEnvelope { get; init; }

    public string? PropertiesJson { get; init; }

    public string? GeometryJson { get; init; }

    public int? GeometrySrid { get; init; }
}

public sealed class FeatureChangeEventOptions
{
    public const string SectionName = "FeatureChangeEvents";

    public int MaxRetainedEvents { get; set; } = 20_000;
}

public sealed class FeatureChangeWebhookOptions
{
    public const string SectionName = "FeatureChangeEvents:Webhook";

    public bool Enabled { get; set; }

    public string? Url { get; set; }

    public string? Secret { get; set; }

    public int MaxAttempts { get; set; } = 5;

    public int InitialBackoffMs { get; set; } = 500;

    public int MaxBackoffMs { get; set; } = 30_000;

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

        if (IPAddress.TryParse(uri.Host, out var literalAddress) && OutboundHttpUrlValidator.IsPrivateOrReservedAddress(literalAddress))
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

        if (uri.IsLoopback || OutboundHttpUrlValidator.IsLocalhostHostName(uri.Host))
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
        => await PinnedDnsConnector.TryResolveAllowedAddressesAsync(uri.DnsSafeHost, hostAddressResolver, cancellationToken)
            .ConfigureAwait(false) is null;

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
