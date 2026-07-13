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

    /// <summary>
    /// Optional substring the response body must contain. When set (with <see cref="ExpectedBodySha256"/>
    /// or on its own), a response that returns <see cref="ExpectedStatusCode"/> but whose body does not
    /// satisfy the assertion is counted as a failure — the "healthy but wrong" correctness gate (#2811).
    /// </summary>
    public string? ExpectedBodyContains { get; init; }

    /// <summary>Optional hex-encoded SHA-256 the full response body must hash to.</summary>
    public string? ExpectedBodySha256 { get; init; }

    /// <summary>Indicates a response-body assertion is configured (a golden query rather than a liveness probe).</summary>
    public bool HasBodyAssertion =>
        !string.IsNullOrWhiteSpace(ExpectedBodyContains) || !string.IsNullOrWhiteSpace(ExpectedBodySha256);
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
/// Runs a synthetic health probe against a deploy target as a provider-independent rollback signal.
/// </summary>
internal interface IDeployHealthProbe
{
    /// <summary>Probes the target and reports how many of the configured samples were unhealthy.</summary>
    Task<DeployHealthProbeResult> ProbeAsync(DeployHealthProbeRequest request, CancellationToken cancellationToken);
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

    // Bound the golden-query body read so a large/streaming response cannot exhaust memory. Golden
    // payloads are small correctness fixtures; anything larger is treated as a mismatch on the read cap.
    private const int MaximumBodyBytes = 4 * 1024 * 1024;

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
        var verifyBody = request.HasBodyAssertion;
        var completionOption = verifyBody
            ? HttpCompletionOption.ResponseContentRead
            : HttpCompletionOption.ResponseHeadersRead;
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
                    .SendAsync(httpRequest, completionOption, timeoutCts.Token)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode != request.ExpectedStatusCode)
                {
                    failures++;
                }
                else if (verifyBody &&
                    !await BodyMatchesAsync(response, request, timeoutCts.Token).ConfigureAwait(false))
                {
                    // A 200 with the WRONG body is exactly the "healthy but corrupt" release: count it
                    // as a failure so the correctness gate blocks auto-promotion (#2811).
                    failures++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A timeout, connection reset, or transport failure is an unhealthy probe — the whole
                // point of the synthetic gate is to catch a target that does not answer cleanly.
                failures++;
            }
        }

        return new DeployHealthProbeResult
        {
            Attempts = samples,
            Failures = failures,
            Detail = verifyBody
                ? $"{failures} of {samples} synthetic checks did not return {request.ExpectedStatusCode} with the expected body content."
                : $"{failures} of {samples} synthetic health checks did not return {request.ExpectedStatusCode}."
        };
    }

    private static async Task<bool> BodyMatchesAsync(
        HttpResponseMessage response,
        DeployHealthProbeRequest request,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes)
            {
                // Oversized body: treat as a mismatch rather than buffering unboundedly.
                return false;
            }

            buffer.Write(chunk, 0, read);
        }

        var body = buffer.ToArray();

        if (!string.IsNullOrWhiteSpace(request.ExpectedBodySha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(body));
            if (!string.Equals(actual, request.ExpectedBodySha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedBodyContains))
        {
            var text = Encoding.UTF8.GetString(body);
            if (!text.Contains(request.ExpectedBodyContains, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
