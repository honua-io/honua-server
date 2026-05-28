// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Shared webhook delivery logic: HMAC signature, signed HTTP POST, and retry with exponential backoff.
/// Used by both the feature-change and manifest-approval webhook dispatchers.
/// </summary>
internal static partial class WebhookDeliveryHelper
{
    internal static HttpMessageHandler CreatePinnedDnsHttpMessageHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        var resolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Cap outbound concurrency per destination to prevent a slow webhook consumer
            // from absorbing the entire thread pool's worth of sockets. The default is
            // unbounded; 64 is enough for hot burst workloads without starving the host.
            MaxConnectionsPerServer = 64,
            ConnectCallback = (context, cancellationToken) =>
                ConnectWithPinnedDnsAsync(context, resolver, cancellationToken)
        };
    }

    /// <summary>
    /// Computes an HMAC-SHA256 signature for webhook payloads using the <c>timestamp.payload</c> message format.
    /// Delegates to <see cref="WebhookSignatureHelper"/> to avoid duplication.
    /// </summary>
    internal static string ComputeSignature(string secret, string timestamp, string payload)
        => WebhookSignatureHelper.ComputeSignature(secret, timestamp, payload);

    /// <summary>
    /// Delivers a signed JSON payload to a webhook endpoint with retry and exponential backoff.
    /// </summary>
    internal static async Task<bool> DeliverWithRetryAsync(
        WebhookDeliveryRequest request,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var timestamp = request.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = ComputeSignature(request.Secret, timestamp, request.Payload);
        var maxAttempts = Math.Max(1, request.MaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RequestTimeoutSeconds)));

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.WebhookUri)
                {
                    Content = new StringContent(request.Payload, Encoding.UTF8, "application/json")
                };
                AddValidatedHeader(httpRequest.Headers, "X-Honua-Event-Id", request.EventId);
                AddValidatedHeader(httpRequest.Headers, "X-Honua-Event-Timestamp", timestamp);
                AddValidatedHeader(httpRequest.Headers, "X-Honua-Signature", $"sha256={signature}");
                AddValidatedHeader(httpRequest.Headers, "Idempotency-Key", request.EventId);

                var client = httpClientFactory.CreateClient(request.HttpClientName);
                using var response = await client.SendAsync(httpRequest, timeoutCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    LogDeliverySucceeded(logger, request.EventId, response.StatusCode);
                    return true;
                }

                var isRetryable = (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                LogDeliveryFailed(logger, request.EventId, attempt, (int)response.StatusCode, isRetryable);
                if (!isRetryable || attempt == maxAttempts)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDeliveryException(logger, request.EventId, attempt, ex);
                if (attempt == maxAttempts)
                {
                    return false;
                }
            }

            var delayMs = ComputeBackoffDelayMilliseconds(request, attempt);
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    internal static int ComputeBackoffDelayMilliseconds(WebhookDeliveryRequest request, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var initialBackoff = Math.Max(1, request.InitialBackoffMs);
        var maxBackoff = Math.Max(1, request.MaxBackoffMs);
        var exponentialDelay = Math.Min((long)initialBackoff << (attempt - 1), maxBackoff);

        if (exponentialDelay <= 1)
        {
            return 1;
        }

        // Apply deterministic per-event jitter so concurrent retries do not herd while
        // preserving reproducible behavior in tests and diagnostics.
        var hash = ComputeDeterministicHash(request.EventId);
        hash = unchecked((hash * 16777619u) ^ (uint)attempt);
        var jitterPercent = (int)(hash % 21u) - 10; // [-10%, +10%]
        var jitteredDelay = exponentialDelay + ((exponentialDelay * jitterPercent) / 100L);

        return (int)Math.Clamp(jitteredDelay, 1L, maxBackoff);
    }

    internal static void AddValidatedHeader(HttpHeaders headers, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var sanitizedValue = SanitizeHeaderValue(value);

        try
        {
            headers.Add(name, sanitizedValue);
        }
        catch (FormatException)
        {
            headers.Add(name, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
        }
    }

    internal static string SanitizeHeaderValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var sanitized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if ((character >= 0x20 && character != 0x7f) || character == '\t')
            {
                sanitized.Append(character);
            }
        }

        return sanitized.Length > 0
            ? sanitized.ToString()
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static uint ComputeDeterministicHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return hash;
        }
    }

    private static async ValueTask<Stream> ConnectWithPinnedDnsAsync(
        SocketsHttpConnectionContext context,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(
                context.DnsEndPoint.Host,
                hostAddressResolver,
                cancellationToken)
            .ConfigureAwait(false);

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var connected = false;

            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                connected = true;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastException = ex;
            }
            finally
            {
                if (!connected)
                {
                    socket.Dispose();
                }
            }
        }

        throw new HttpRequestException("Unable to establish a secure connection to the webhook host.", lastException);
    }

    private static async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (IsPrivateOrReservedAddress(literalAddress))
            {
                throw new HttpRequestException(FeatureChangeWebhookUrlValidation.DisallowedAddressMessage);
            }

            return [literalAddress];
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            throw new HttpRequestException(FeatureChangeWebhookUrlValidation.DisallowedAddressMessage);
        }
        catch (ArgumentException)
        {
            throw new HttpRequestException(FeatureChangeWebhookUrlValidation.DisallowedAddressMessage);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(FeatureChangeWebhookUrlValidation.DisallowedAddressMessage);
        }

        foreach (var address in addresses)
        {
            if (IsPrivateOrReservedAddress(address))
            {
                throw new HttpRequestException(FeatureChangeWebhookUrlValidation.DisallowedAddressMessage);
            }
        }

        return addresses;
    }

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

    [LoggerMessage(EventId = 9001, Level = LogLevel.Debug, Message = "Webhook delivery succeeded for event {EventId} with status {StatusCode}.")]
    private static partial void LogDeliverySucceeded(ILogger logger, string eventId, System.Net.HttpStatusCode statusCode);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "Webhook delivery failed for event {EventId} on attempt {Attempt} with status {StatusCode}. Retryable={Retryable}.")]
    private static partial void LogDeliveryFailed(ILogger logger, string eventId, int attempt, int statusCode, bool retryable);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Warning, Message = "Webhook delivery threw for event {EventId} on attempt {Attempt}.")]
    private static partial void LogDeliveryException(ILogger logger, string eventId, int attempt, Exception exception);
}

/// <summary>
/// Parameters for a webhook delivery attempt.
/// </summary>
internal readonly record struct WebhookDeliveryRequest
{
    /// <summary>
    /// Pre-serialized JSON payload.
    /// </summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Event identifier used for headers and idempotency.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Event timestamp used for signature generation.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Target webhook URI.
    /// </summary>
    public required Uri WebhookUri { get; init; }

    /// <summary>
    /// HMAC shared secret.
    /// </summary>
    public required string Secret { get; init; }

    /// <summary>
    /// Named HTTP client to use for delivery.
    /// </summary>
    public required string HttpClientName { get; init; }

    /// <summary>
    /// Maximum delivery attempts.
    /// </summary>
    public required int MaxAttempts { get; init; }

    /// <summary>
    /// Base retry delay in milliseconds.
    /// </summary>
    public required int InitialBackoffMs { get; init; }

    /// <summary>
    /// Maximum retry delay in milliseconds.
    /// </summary>
    public required int MaxBackoffMs { get; init; }

    /// <summary>
    /// Per-request timeout in seconds.
    /// </summary>
    public required int RequestTimeoutSeconds { get; init; }
}
