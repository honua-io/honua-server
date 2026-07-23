// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.ControlPlane;

/// <summary>
/// A request to run a synthetic health probe against a deploy target during the bake window. The
/// probe issues <see cref="Samples"/> sequential <c>GET</c> requests and counts how many do not
/// return <see cref="ExpectedStatusCode"/> (or fail outright), so a configurable failure threshold
/// can promote "unhealthy" to a first-class rollback trigger alongside the error-rate/latency gate.
/// </summary>
internal sealed record DeployHealthProbeRequest
{
    /// <summary>The absolute HTTPS URL to probe, typically the canary's <c>/healthz/ready</c> endpoint.</summary>
    public required string Url { get; init; }

    /// <summary>Number of sequential probe requests to issue (clamped to a sane bound at runtime).</summary>
    public int Samples { get; init; } = 3;

    /// <summary>HTTP status code that indicates a healthy response.</summary>
    public int ExpectedStatusCode { get; init; } = 200;

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>
/// Outcome of a synthetic health probe. <see cref="Validated"/> is <see langword="false"/> when the
/// configured URL failed outbound-URL validation (a configuration error rather than a health signal),
/// so the gate can hold for an operator rather than treating a misconfiguration as an unhealthy target.
/// </summary>
internal sealed record DeployHealthProbeResult
{
    /// <summary>Number of probe requests actually issued.</summary>
    public int Attempts { get; init; }

    /// <summary>Number of probe requests that did not return the expected status (or threw).</summary>
    public int Failures { get; init; }

    /// <summary>Human-readable detail suitable for an operation phase message.</summary>
    public string? Detail { get; init; }

    /// <summary>Whether the probe URL passed outbound-URL validation and the probe ran.</summary>
    public bool Validated { get; init; } = true;
}

/// <summary>
/// A request to run a golden-query correctness probe against a deploy target during the bake window. Unlike
/// <see cref="DeployHealthProbeRequest"/> (which only compares the HTTP status), the golden query fetches a
/// known endpoint and asserts the <em>response body</em> matches an operator-declared expectation — a SHA-256
/// checksum and/or a required substring — so a "healthy-but-corrupt" release (200 OK but wrong/garbled
/// payload) is caught before it is auto-promoted (honua-server#2811).
/// </summary>
internal sealed record DeployGoldenQueryRequest
{
    /// <summary>The absolute HTTPS URL whose response body is the golden-query correctness signal.</summary>
    public required string Url { get; init; }

    /// <summary>Expected lowercase hex SHA-256 of the full response body, when checksum matching is configured.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>A substring the response body must contain, when substring matching is configured.</summary>
    public string? ExpectedBodyContains { get; init; }

    /// <summary>HTTP status code that indicates a servable response (the body is only checked on this status).</summary>
    public int ExpectedStatusCode { get; init; } = 200;

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Maximum response body bytes read before the probe treats the response as a mismatch (unbounded-buffer guard).</summary>
    public int MaxResponseBytes { get; init; } = 1_048_576;
}

/// <summary>
/// Outcome of a golden-query correctness probe. <see cref="Validated"/> is <see langword="false"/> when the
/// configured URL failed outbound-URL validation (a configuration error, not a correctness signal), so the
/// gate holds for an operator rather than promoting past an unverified correctness check.
/// </summary>
internal sealed record DeployGoldenQueryResult
{
    /// <summary>Whether the response body satisfied every configured expectation (checksum and/or substring).</summary>
    public bool Matched { get; init; }

    /// <summary>Human-readable detail suitable for an operation phase message.</summary>
    public string? Detail { get; init; }

    /// <summary>Whether the golden-query URL passed outbound-URL validation and the probe ran.</summary>
    public bool Validated { get; init; } = true;
}

/// <summary>
/// Runs a synthetic health probe against a deploy target as a provider-independent rollback signal.
/// </summary>
internal interface IDeployHealthProbe
{
    /// <summary>Probes the target and reports how many of the configured samples were unhealthy.</summary>
    Task<DeployHealthProbeResult> ProbeAsync(DeployHealthProbeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the golden-query endpoint and reports whether the response body matched the declared
    /// checksum/substring expectation — a correctness gate beyond the status-only health probe.
    /// </summary>
    Task<DeployGoldenQueryResult> ProbeGoldenQueryAsync(DeployGoldenQueryRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IDeployHealthProbe"/> that issues real HTTPS <c>GET</c> requests to the
/// configured health endpoint. The URL is validated through <see cref="OutboundHttpUrlValidator"/>
/// (HTTPS-only, no private/loopback destinations) for the same SSRF protection the metrics gate uses.
/// </summary>
internal sealed class HttpDeployHealthProbe(IHttpClientFactory httpClientFactory) : IDeployHealthProbe
{
    private const int MinimumSamples = 1;
    private const int MaximumSamples = 20;

    public async Task<DeployHealthProbeResult> ProbeAsync(
        DeployHealthProbeRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await OutboundHttpUrlValidator
            .ValidateAsync(request.Url, cancellationToken)
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
        var client = httpClientFactory.CreateClient("control-plane-telemetry");
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
            Detail = $"{failures} of {samples} synthetic health checks did not return {request.ExpectedStatusCode}."
        };
    }

    public async Task<DeployGoldenQueryResult> ProbeGoldenQueryAsync(
        DeployGoldenQueryRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await OutboundHttpUrlValidator
            .ValidateAsync(request.Url, cancellationToken)
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
        var client = httpClientFactory.CreateClient("control-plane-telemetry");

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

            var mismatches = new List<string>();

            if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
            {
                var actual = Convert.ToHexStringLower(SHA256.HashData(body));
                if (!string.Equals(actual, request.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"body checksum {actual} did not match expected {request.ExpectedSha256.Trim()}");
                }
            }

            if (!string.IsNullOrWhiteSpace(request.ExpectedBodyContains))
            {
                var text = Encoding.UTF8.GetString(body);
                if (!text.Contains(request.ExpectedBodyContains, StringComparison.Ordinal))
                {
                    mismatches.Add("response body did not contain the required golden token");
                }
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
