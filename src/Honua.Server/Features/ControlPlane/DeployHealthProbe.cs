// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal sealed record DeployHealthProbeRequest
{
    public required string Url { get; init; }

    public int Samples { get; init; } = 3;

    public int ExpectedStatusCode { get; init; } = 200;

    public int TimeoutSeconds { get; init; } = 5;
}

internal sealed record DeployHealthProbeResult
{
    public int Attempts { get; init; }

    public int Failures { get; init; }

    public string? Detail { get; init; }

    public bool Validated { get; init; } = true;
}

// The golden query asserts the body, so a success status with a wrong payload is caught before promotion.
internal sealed record DeployGoldenQueryRequest
{
    public required string Url { get; init; }

    public string? ExpectedSha256 { get; init; }

    public string? ExpectedBodyContains { get; init; }

    // The response body must not contain this wrong-result marker.
    public string? ForbiddenBodyContains { get; init; }

    public int ExpectedStatusCode { get; init; } = 200;

    public int TimeoutSeconds { get; init; } = 5;

    public int MaxResponseBytes { get; init; } = 1_048_576;
}

internal sealed record DeployGoldenQueryResult
{
    public bool Matched { get; init; }

    public string? Detail { get; init; }

    public bool Validated { get; init; } = true;
}

internal interface IDeployHealthProbe
{
    Task<DeployHealthProbeResult> ProbeAsync(DeployHealthProbeRequest request, CancellationToken cancellationToken);

    Task<DeployGoldenQueryResult> ProbeGoldenQueryAsync(DeployGoldenQueryRequest request, CancellationToken cancellationToken);
}

internal sealed class HttpDeployHealthProbe(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ControlPlaneOptions>? controlPlaneOptions = null) : IDeployHealthProbe
{
    private const int MinimumSamples = 1;
    private const int MaximumSamples = 20;
    private const int ReadinessBodyInspectionLimit = 65_536;

    private bool AllowPrivateNetworkProbes => controlPlaneOptions?.CurrentValue.AllowPrivateNetworkProbes == true;

    public async Task<DeployHealthProbeResult> ProbeAsync(
        DeployHealthProbeRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await DeployProbeUrlPolicy
            .ValidateAsync(request.Url, AllowPrivateNetworkProbes, cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsValid || validation.Uri is null)
        {
            return new DeployHealthProbeResult
            {
                Attempts = 0,
                Failures = 0,
                Validated = false,
                Detail = $"Synthetic health probe URL {validation.ErrorMessage ?? "must be a valid HTTPS URL."}"
            };
        }

        var samples = Math.Clamp(request.Samples <= 0 ? 1 : request.Samples, MinimumSamples, MaximumSamples);
        var timeoutSeconds = Math.Max(1, request.TimeoutSeconds);
        var client = httpClientFactory.CreateClient(ControlPlaneHttpClients.Probe);
        var failures = 0;

        for (var attempt = 0; attempt < samples; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, validation.Uri);
                using var response = await client
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode != request.ExpectedStatusCode)
                {
                    failures++;
                    continue;
                }

                // A readiness endpoint that answers the expected 2xx with an error envelope or an
                // "Unhealthy" report is not healthy. An oversized body cannot be inspected
                // and is treated as a failed check rather than assumed healthy.
                if (DeployProbeBodyInspector.IsSuccessStatus(request.ExpectedStatusCode))
                {
                    var body = await ReadBoundedBodyAsync(response, ReadinessBodyInspectionLimit, timeoutCts.Token).ConfigureAwait(false);
                    if (body is null || DeployProbeBodyInspector.DescribeErrorEnvelope(body) != null)
                    {
                        failures++;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional catch-all: per-sample loop of a synthetic health-check gate. A
            // timeout, connection reset, or transport failure is itself an unhealthy probe —
            // the whole point of the gate is to catch a target that does not answer cleanly.
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                failures++;
            }
        }

        return new DeployHealthProbeResult
        {
            Attempts = samples,
            Failures = failures,
            Detail = $"{failures} of {samples} synthetic health checks did not return a healthy {request.ExpectedStatusCode} response."
        };
    }

    public async Task<DeployGoldenQueryResult> ProbeGoldenQueryAsync(
        DeployGoldenQueryRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await DeployProbeUrlPolicy
            .ValidateAsync(request.Url, AllowPrivateNetworkProbes, cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsValid || validation.Uri is null)
        {
            return new DeployGoldenQueryResult
            {
                Matched = false,
                Validated = false,
                Detail = $"Golden-query URL {validation.ErrorMessage ?? "must be a valid HTTPS URL."}"
            };
        }

        var timeoutSeconds = Math.Max(1, request.TimeoutSeconds);
        var maxBytes = Math.Max(1, request.MaxResponseBytes);
        var client = httpClientFactory.CreateClient(ControlPlaneHttpClients.Probe);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, validation.Uri);
            using var response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if ((int)response.StatusCode != request.ExpectedStatusCode)
            {
                return new DeployGoldenQueryResult
                {
                    Matched = false,
                    Detail = $"Golden-query endpoint returned status {(int)response.StatusCode}, expected {request.ExpectedStatusCode}."
                };
            }

            var body = await ReadBoundedBodyAsync(response, maxBytes, timeoutCts.Token).ConfigureAwait(false);
            if (body is null)
            {
                return new DeployGoldenQueryResult
                {
                    Matched = false,
                    Detail = $"Golden-query response body exceeded the {maxBytes}-byte inspection limit and was treated as a mismatch."
                };
            }

            // A 2xx that carries an error envelope (GeoServices {"error":...}, OGC exception report,
            // problem details) is a failed query, not a result, whatever else it contains.
            if (DeployProbeBodyInspector.IsSuccessStatus(request.ExpectedStatusCode) &&
                DeployProbeBodyInspector.DescribeErrorEnvelope(body) is { } envelope)
            {
                return new DeployGoldenQueryResult
                {
                    Matched = false,
                    Detail = $"Golden-query correctness gate failed: the endpoint returned HTTP {(int)response.StatusCode} with {envelope} instead of a result."
                };
            }

            var mismatches = new List<string>();

            if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
            {
                var actual = Convert.ToHexStringLower(SHA256.HashData(body));
                if (!string.Equals(actual, request.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"body checksum {actual} did not match expected {request.ExpectedSha256.Trim()}");
                }
            }

            var text = Encoding.UTF8.GetString(body);
            if (!string.IsNullOrWhiteSpace(request.ExpectedBodyContains) &&
                !text.Contains(request.ExpectedBodyContains, StringComparison.Ordinal))
            {
                mismatches.Add("response body did not contain the required golden token");
            }

            if (!string.IsNullOrWhiteSpace(request.ForbiddenBodyContains) &&
                text.Contains(request.ForbiddenBodyContains, StringComparison.Ordinal))
            {
                mismatches.Add("response body contained the configured wrong-result marker");
            }

            return mismatches.Count == 0
                ? new DeployGoldenQueryResult { Matched = true, Detail = "Golden-query correctness gate passed." }
                : new DeployGoldenQueryResult
                {
                    Matched = false,
                    Detail = $"Golden-query correctness gate failed: {string.Join("; ", mismatches)}."
                };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentional catch-all: this is the correctness gate for a deploy's golden-query
        // probe. A timeout, reset, or transport failure means correctness could not be
        // confirmed; treating that as a mismatch keeps the gate fail-safe — an unverifiable
        // release is not auto-promoted.
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            return new DeployGoldenQueryResult
            {
                Matched = false,
                Detail = "Golden-query endpoint could not be reached to confirm release correctness."
            };
        }
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var rented = new byte[8192];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            buffer.Write(rented, 0, read);
        }

        return buffer.ToArray();
    }
}

// Planning and the runtime share one destination rule, so the plan refuses the URLs the probe would refuse.
internal static class DeployProbeUrlPolicy
{
    internal const string AllowPrivateNetworksEnvVar = "HONUA_DEPLOY_PROBE_ALLOW_PRIVATE_NETWORKS";
    private const string LinkLocalBlockedMessage =
        "resolves to a link-local or instance-metadata address, which is unconditionally blocked.";

    public static Task<OutboundHttpUrlValidationResult> ValidateAsync(string url, CancellationToken cancellationToken)
        => ValidateAsync(url, allowPrivateNetworks: false, cancellationToken);

    public static Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string url,
        bool allowPrivateNetworks,
        CancellationToken cancellationToken)
        => ValidateAsync(url, allowPrivateNetworks, Dns.GetHostAddressesAsync, cancellationToken);

    internal static async Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string url,
        bool allowPrivateNetworks,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        var allowPrivate = allowPrivateNetworks || IsPrivateNetworksEnvEnabled();
        var result = await OutboundHttpUrlValidator
            .ValidateAsync(url, hostAddressResolver, allowPrivate, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsValid || result.Uri is null || !allowPrivate)
        {
            return result;
        }

        return await EnforceLinkLocalBlockAsync(result.Uri, hostAddressResolver, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsPrivateNetworksEnvEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable(AllowPrivateNetworksEnvVar),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static async Task<OutboundHttpUrlValidationResult> EnforceLinkLocalBlockAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            return IsLinkLocalAddress(literalAddress)
                ? OutboundHttpUrlValidationResult.Failure(LinkLocalBlockedMessage, OutboundHttpUrlFailureReason.DisallowedAddress)
                : OutboundHttpUrlValidationResult.Success(uri);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return OutboundHttpUrlValidationResult.Failure(
                "could not be verified because host name resolution is currently unavailable; the destination was not contacted.",
                OutboundHttpUrlFailureReason.HostResolutionUnavailable);
        }
        catch (ArgumentException)
        {
            return OutboundHttpUrlValidationResult.Failure(
                LinkLocalBlockedMessage,
                OutboundHttpUrlFailureReason.DisallowedAddress);
        }

        if (addresses.Length == 0 || addresses.Any(IsLinkLocalAddress))
        {
            return OutboundHttpUrlValidationResult.Failure(
                LinkLocalBlockedMessage,
                OutboundHttpUrlFailureReason.DisallowedAddress);
        }

        return OutboundHttpUrlValidationResult.Success(uri);
    }

    internal static bool IsLinkLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            {
                return true;
            }

            if (bytes.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0 &&
                ((bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0) ||
                 (bytes[8] == 0xff && bytes[9] == 0xff && bytes[10] == 0 && bytes[11] == 0)))
            {
                return IsLinkLocalAddress(new IPAddress(bytes.AsSpan(12, 4)));
            }

            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)
            {
                return IsLinkLocalAddress(new IPAddress(bytes.AsSpan(12, 4)));
            }

            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return IsLinkLocalAddress(new IPAddress(bytes.AsSpan(2, 4)));
            }
        }

        return false;
    }
}

internal static class DeployProbeBodyInspector
{
    public static bool IsSuccessStatus(int statusCode) => statusCode is >= 200 and <= 299;

    public static string? DescribeErrorEnvelope(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body).TrimStart('\uFEFF').Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '{')
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error) &&
                    (error.ValueKind == JsonValueKind.Object ||
                     (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))))
                {
                    return "a JSON error envelope (top-level \"error\")";
                }

                if (root.TryGetProperty("status", out var status))
                {
                    if (status.ValueKind == JsonValueKind.String &&
                        string.Equals(status.GetString(), "Unhealthy", StringComparison.OrdinalIgnoreCase))
                    {
                        return "an Unhealthy health report";
                    }

                    if (status.ValueKind == JsonValueKind.Number &&
                        status.TryGetInt32(out var problemStatus) &&
                        problemStatus >= 400 &&
                        root.TryGetProperty("title", out _))
                    {
                        return "a problem-details error envelope";
                    }
                }

                if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
                    root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                {
                    return "an OGC API exception";
                }
            }
            catch (JsonException)
            {
                // Not JSON after all: leave the verdict to the operator-declared expectations.
                return null;
            }

            return null;
        }

        if (text.Contains("ExceptionReport", StringComparison.Ordinal))
        {
            return "an OGC exception report";
        }

        return string.Equals(text, "Unhealthy", StringComparison.OrdinalIgnoreCase)
            ? "an Unhealthy health report"
            : null;
    }
}
