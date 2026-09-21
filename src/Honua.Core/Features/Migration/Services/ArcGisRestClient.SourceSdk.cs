// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Migration.Domain;
using Honua.Sdk.GeoServices.FeatureServer;
using Honua.Sdk.GeoServices.FeatureServer.Exceptions;
using Polly;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Thin adapter over the published <c>Honua.Sdk.GeoServices</c> FeatureServer client (#4599).
/// The SDK owns the ArcGIS protocol: source-root addressing, query parameters, response models,
/// HTTP-200 error envelopes and the bounded body read. The server keeps what it owns for every
/// source request: the outbound URL/SSRF guard, the pinned-DNS no-redirect transport (the injected
/// <see cref="HttpClient"/>), per-source credential headers, per-attempt timeouts, the retry policy
/// (429 <c>Retry-After</c>, 5xx, transport faults) and sanitized exceptions for job state and logs.
/// </summary>
internal sealed partial class ArcGisRestClient
{
    private const string DefaultSdkEnvelopeMessage = "GeoServices returned an error.";

    private async Task<T> ExecuteSourceRequestAsync<T>(
        string normalizedUrl,
        string resourceUrl,
        int timeoutSeconds,
        int maxRetries,
        GeoservicesCredentialDescriptor? credentials,
        Func<HonuaFeatureServerClient, string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var sourceHttpClient = CreateSourceHttpClient(credentials);
        return await ExecuteSourceRequestAsync(
            sourceHttpClient,
            normalizedUrl,
            resourceUrl,
            timeoutSeconds,
            maxRetries,
            credentials,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ExecuteSourceRequestAsync<T>(
        HttpClient sourceHttpClient,
        string normalizedUrl,
        string resourceUrl,
        int timeoutSeconds,
        int maxRetries,
        GeoservicesCredentialDescriptor? credentials,
        Func<HonuaFeatureServerClient, string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await EnsureSafeOutboundUriAsync(normalizedUrl, cancellationToken).ConfigureAwait(false);

        if (!ArcGisServiceRoot.TryParse(new Uri(normalizedUrl, UriKind.Absolute), out var serviceRoot))
        {
            throw new HttpRequestException(InvalidServiceRootUrlMessage);
        }

        var client = new HonuaFeatureServerClient(
            sourceHttpClient,
            serviceRoot.ToClientOptions(MigrationHttpContentReader.DefaultMaxResponseBytes));
        var options = BuildHttpOptions(maxRetries);
        var policy = CreateSourceRequestPolicy<T>(options, maxRetries, cancellationToken);

        try
        {
            return await policy.ExecuteAsync(
                async ct =>
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    return await operation(client, serviceRoot.ServiceId, timeoutCts.Token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TranslateSourceException(ex, resourceUrl, credentials) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Creates the per-call client handed to the SDK. It forwards every SDK request through the
    /// injected pinned-DNS client and applies this source's credentials as headers, so no secret is
    /// ever placed in a request URL.
    /// </summary>
    private HttpClient CreateSourceHttpClient(GeoservicesCredentialDescriptor? credentials)
        => new(new SourceTransportHandler(_httpClient, credentials), disposeHandler: true)
        {
            // The injected client enforces its own timeout; per-attempt timeouts are applied above.
            Timeout = Timeout.InfiniteTimeSpan
        };

    private IAsyncPolicy<T> CreateSourceRequestPolicy<T>(
        ResiliencePolicyOptions options,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        var builder = Policy<T>
            .Handle<HttpRequestException>()
            .Or<HonuaFeatureServerException>(IsRetryableTransportFailure)
            .Or<OperationCanceledException>(_ => !cancellationToken.IsCancellationRequested);

        return ResiliencePolicyFactory.CreateStandardPolicy(
            builder,
            options,
            onRetry: (result, delay, attempt) =>
                Log.RetryingRequest(_logger, attempt, maxRetries, delay.TotalSeconds, GetSourceFailureMessage(result.Exception)),
            retryDelayProvider: (attempt, result, _) =>
            {
                var delay = result.Exception is HonuaFeatureServerException { RetryAfter: { } retryAfter }
                    ? retryAfter
                    : options.GetDelay(attempt);
                return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            });
    }

    // Only transport statuses are retried, matching the pre-SDK client: an HTTP-200 error envelope
    // (GeoServicesErrorCode set) is a definitive source answer, and auth statuses never recover.
    private static bool IsRetryableTransportFailure(HonuaFeatureServerException exception)
        => exception.GeoServicesErrorCode is null
            && ((int)exception.StatusCode >= 500 || exception.StatusCode == HttpStatusCode.TooManyRequests);

    private static string GetSourceFailureMessage(Exception? exception) => exception switch
    {
        null => "Unknown failure",
        HonuaFeatureServerException sdkException => $"HTTP {(int)sdkException.StatusCode}",
        _ => RedactArcGisTokenParameters(exception.Message)
    };

    /// <summary>
    /// Maps SDK failures onto the exceptions the import pipeline already classifies. Source response
    /// bodies are never copied into the translated exception.
    /// </summary>
    private static Exception? TranslateSourceException(
        Exception exception,
        string resourceUrl,
        GeoservicesCredentialDescriptor? credentials)
    {
        switch (exception)
        {
            case HonuaFeatureServerResponseTooLargeException tooLarge:
                return new HttpRequestException(tooLarge.DeclaredContentLength is { } declared
                    ? $"Migration source response declares {declared} bytes, exceeding the {tooLarge.MaxResponseBytes}-byte limit."
                    : $"Migration source response exceeded the {tooLarge.MaxResponseBytes}-byte limit.");

            case HonuaFeatureServerException { GeoServicesErrorCode: { } errorCode } envelope:
                return CreateArcGisResponseException(
                    new ArcGisError
                    {
                        Code = errorCode,
                        Message = string.Equals(envelope.Message, DefaultSdkEnvelopeMessage, StringComparison.Ordinal)
                            ? null
                            : envelope.Message,
                        Details = envelope.Details is { Count: > 0 } details ? [.. details] : null
                    },
                    resourceUrl,
                    credentials);

            case HonuaFeatureServerException { StatusCode: HttpStatusCode.OK }:
                return new InvalidOperationException("Failed to deserialize response");

            case HonuaFeatureServerException transport:
                var statusCode = (int)transport.StatusCode;
                if (statusCode is 401 or 403 or 498 or 499)
                {
                    return CreateAuthenticationException(statusCode, resourceUrl, credentials);
                }

                return new HttpRequestException(
                    $"Response status code does not indicate success: {statusCode} ({transport.StatusCode}).",
                    inner: null,
                    transport.StatusCode);

            default:
                return null;
        }
    }

    // ArcGIS never advertises 0 for maxRecordCount or a wkid; the SDK reports an omitted member as 0.
    private static int? AdvertisedOrNull(int? value) => value is > 0 ? value : null;

    private static JsonElement? GetAdditionalElement(Dictionary<string, JsonElement>? additionalProperties, string name)
        => additionalProperties is not null
            && additionalProperties.TryGetValue(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    private static string? GetAdditionalString(Dictionary<string, JsonElement>? additionalProperties, string name)
        => GetAdditionalElement(additionalProperties, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;

    /// <summary>
    /// Sends SDK-built requests through the injected server transport. A request message can only be
    /// sent by one <see cref="HttpClient"/>, so each is re-issued as a forwarded message that shares
    /// the SDK-owned content.
    /// </summary>
    private sealed class SourceTransportHandler(
        HttpClient transport,
        GeoservicesCredentialDescriptor? credentials) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content,
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };

            try
            {
                foreach (var header in request.Headers)
                {
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                ApplyAuthorizationHeader(forwarded, credentials);
                return await transport
                    .SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // The SDK owns and disposes the original request content.
                forwarded.Content = null;
            }
        }
    }
}
