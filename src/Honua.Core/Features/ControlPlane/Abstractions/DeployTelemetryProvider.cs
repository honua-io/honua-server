// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.ControlPlane;

/// <summary>
/// Provider-neutral view of a configured telemetry connection passed to an
/// <see cref="IDeployTelemetryProviderEvaluator"/>. The server maps its internal connection
/// options onto this descriptor so cloud provider evaluators (which live in cloud-specific
/// assemblies) do not depend on the server's configuration types.
/// </summary>
public sealed record DeployTelemetryConnectionDescriptor
{
    /// <summary>Stable identifier of the telemetry connection.</summary>
    public required string ConnectionId { get; init; }

    /// <summary>Metrics backend selector (for example <c>prometheus</c> or <c>cloudwatch</c>).</summary>
    public required string Provider { get; init; }

    /// <summary>
    /// For HTTP providers, the query API base URL. For cloud providers, the regional endpoint used
    /// only to infer a region when <see cref="Region"/> is unset.
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Relative query path for HTTP providers (for example <c>/api/v1/query</c>).</summary>
    public string QueryPath { get; init; } = "/api/v1/query";

    /// <summary>Optional outbound auth header name for HTTP providers.</summary>
    public string? AuthHeaderName { get; init; }

    /// <summary>Optional outbound auth header value for HTTP providers.</summary>
    public string? AuthHeaderValue { get; init; }

    /// <summary>Optional cloud region (for example <c>us-east-1</c>) for cloud providers.</summary>
    public string? Region { get; init; }

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Explicit operator opt-in that relaxes the outbound SSRF guard for this connection so a
    /// private-network or loopback metrics endpoint (for example an on-prem Prometheus at
    /// <c>http://localhost:9090</c>) can be queried. Defaults to <see langword="false"/>, keeping the
    /// strict HTTPS-only, no-private-destination posture.
    /// </summary>
    public bool AllowPrivateNetworks { get; init; }
}

/// <summary>
/// Provider-neutral deploy telemetry policy: the error-rate / latency / sample-count thresholds and
/// the per-signal query strings to evaluate, independent of which metrics backend runs them.
/// </summary>
public sealed record DeployTelemetryPolicyDescriptor
{
    /// <summary>Identifier of the telemetry connection to query.</summary>
    public required string ConnectionId { get; init; }

    /// <summary>Backend-dialect query that yields the error rate, or <c>null</c> when unused.</summary>
    public string? ErrorRateQuery { get; init; }

    /// <summary>
    /// Backend-dialect query that yields the error <em>count</em> over the evaluation window, or
    /// <c>null</c> when unused. It replaces <see cref="ErrorRateQuery"/>: the gate divides the count by
    /// the sample count read for the same window. A provider that can tell "the query matched no
    /// series" apart from a failed read reports that as <see cref="DeployTelemetryReadings.ErrorCountSeriesAbsent"/>,
    /// and the gate reads it as zero errors only when the sample floor is met (honua-server#6474).
    /// </summary>
    public string? ErrorCountQuery { get; init; }

    /// <summary>Maximum acceptable error rate before rollback is recommended.</summary>
    public double? ErrorRateThreshold { get; init; }

    /// <summary>Backend-dialect query that yields p95 latency in milliseconds, or <c>null</c> when unused.</summary>
    public string? LatencyP95Query { get; init; }

    /// <summary>Maximum acceptable p95 latency in milliseconds before rollback is recommended.</summary>
    public double? LatencyP95ThresholdMs { get; init; }

    /// <summary>Backend-dialect query that yields the observed sample count, or <c>null</c> when unused.</summary>
    public string? MinimumSampleQuery { get; init; }

    /// <summary>Minimum sample count required before the gate settles instead of waiting.</summary>
    public double? MinimumSampleCount { get; init; }

    /// <summary>
    /// Indicates the operator supplied explicit query overrides (rather than relying on a built-in
    /// preset). Cloud providers without a preset query dialect require this.
    /// </summary>
    public bool HasExplicitQueryOverride { get; init; }

    /// <summary>
    /// Optional maximum age for a queried sample (honua-server#4617). Providers that expose a
    /// per-sample observation timestamp (for example Prometheus) should treat a sample older than
    /// this bound as absent rather than a usable reading. <see langword="null"/> disables the check.
    /// </summary>
    public TimeSpan? MaximumEvidenceStaleness { get; init; }
}

/// <summary>
/// Raw metric readings for the configured deploy telemetry signals. A <c>null</c> reading means the
/// signal was not configured or no datapoint was available yet.
/// </summary>
public sealed record DeployTelemetryReadings
{
    /// <summary>Observed sample count, or <c>null</c> when unavailable.</summary>
    public double? SampleCount { get; init; }

    /// <summary>Observed error rate, or <c>null</c> when unavailable.</summary>
    public double? ErrorRate { get; init; }

    /// <summary>
    /// Observed error count for <see cref="DeployTelemetryPolicyDescriptor.ErrorCountQuery"/>, or
    /// <c>null</c> when unavailable or when the query matched no series.
    /// </summary>
    public double? ErrorCount { get; init; }

    /// <summary>
    /// <see langword="true"/> only when the error-count query completed and matched no series or rows
    /// at all. AWS omits ALB <c>HTTPCode_Target_5XX_Count</c> and Lambda <c>Errors</c> datapoints for
    /// periods without errors, so this is how a healthy candidate looks. A failed, partial or ambiguous
    /// read never sets it. The gate treats it as zero errors only when the sample count read for the
    /// same window meets the sample floor (honua-server#6474).
    /// </summary>
    public bool ErrorCountSeriesAbsent { get; init; }

    /// <summary>Observed p95 latency in milliseconds, or <c>null</c> when unavailable.</summary>
    public double? LatencyP95 { get; init; }
}

/// <summary>
/// Reads the configured deploy telemetry signals for a single metrics backend
/// (Prometheus, CloudWatch, …). Provider selection happens in the server's dispatcher by matching
/// <see cref="Provider"/> against the connection's provider value; implementations only translate
/// the policy's query strings into their native dialect and return raw readings.
/// </summary>
public interface IDeployTelemetryProviderEvaluator
{
    /// <summary>
    /// The connection provider value (case-insensitive) this evaluator handles,
    /// for example <c>prometheus</c> or <c>cloudwatch</c>.
    /// </summary>
    string Provider { get; }

    /// <summary>
    /// Reads the configured signals for the supplied policy/connection pair.
    /// </summary>
    Task<DeployTelemetryReadings> ReadAsync(
        DeployTelemetryPolicyDescriptor policy,
        DeployTelemetryConnectionDescriptor connection,
        CancellationToken cancellationToken);
}
