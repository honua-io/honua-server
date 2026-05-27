// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Represents a recent error captured by the in-memory buffer.
/// </summary>
internal sealed class RecentErrorEntry
{
    /// <summary>
    /// Timestamp when the error was captured (UTC).
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Correlation ID for the request that produced the error.
    /// </summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>
    /// Request path that produced the error.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// HTTP status code for the error response.
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// Sanitized error message.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Response model for recent errors endpoint.
/// </summary>
internal sealed class RecentErrorsResponse
{
    /// <summary>
    /// Maximum number of errors retained in the buffer.
    /// </summary>
    public int Capacity { get; init; }

    /// <summary>
    /// Identifier for the current node that generated the response.
    /// </summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>
    /// Recent errors, ordered newest-first.
    /// </summary>
    public IReadOnlyList<RecentErrorEntry> Errors { get; init; } = Array.Empty<RecentErrorEntry>();
}

/// <summary>
/// Response model for observability status.
/// </summary>
internal sealed class ObservabilityStatusResponse
{
    /// <summary>
    /// Timestamp when the status snapshot was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Realtime admin status capability.
    /// </summary>
    public ObservabilityRealtimeStatus Realtime { get; init; } = new();

    /// <summary>
    /// Whether tracing is enabled.
    /// </summary>
    public bool TracingEnabled { get; init; }

    /// <summary>
    /// Whether metrics collection/export is enabled.
    /// </summary>
    public bool MetricsEnabled { get; init; }

    /// <summary>
    /// Whether OpenTelemetry log export plumbing is enabled.
    /// </summary>
    public bool LogsEnabled { get; init; }

    /// <summary>
    /// Whether an OTLP endpoint value is configured.
    /// </summary>
    public bool OtlpConfigured { get; init; }

    /// <summary>
    /// Whether the configured OTLP endpoint is a valid absolute HTTP(S) URI.
    /// </summary>
    public bool OtlpEndpointValid { get; init; }

    /// <summary>
    /// Configured OTLP endpoint (if any).
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>
    /// Whether OTLP exporter headers are configured without exposing their values.
    /// </summary>
    public bool OtlpHeadersConfigured { get; init; }

    /// <summary>
    /// Overall OTLP exporter state: disabled, notConfigured, configured, or misconfigured.
    /// </summary>
    public string OtlpExporterState { get; init; } = "notConfigured";

    /// <summary>
    /// Trace export state for the configured telemetry pipeline.
    /// </summary>
    public string TraceExportState { get; init; } = "notConfigured";

    /// <summary>
    /// Metrics export state for the configured telemetry pipeline.
    /// </summary>
    public string MetricsExportState { get; init; } = "notConfigured";

    /// <summary>
    /// Log export state for the configured telemetry pipeline.
    /// </summary>
    public string LogExportState { get; init; } = "notConfigured";

    /// <summary>
    /// Last exporter error observed by the server, when the exporter exposes one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LastExportError { get; init; }

    /// <summary>
    /// Runtime probe state for the configured OTLP endpoint, distinct from
    /// configuration-derived <see cref="OtlpExporterState"/>. One of
    /// <c>unknown</c>, <c>disabled</c>, <c>healthy</c>, <c>unreachable</c>,
    /// <c>authFailure</c>, <c>telemetryDisabled</c>, or <c>exporterMisconfigured</c>.
    /// </summary>
    public string OtlpProbeState { get; init; } = "unknown";

    /// <summary>
    /// Last sanitized error observed by the probe (timeouts, connection refused,
    /// HTTP 401/403). Null when probe state is <c>healthy</c> or <c>disabled</c>.
    /// </summary>
    public string? OtlpLastProbeError { get; init; }

    /// <summary>
    /// When the OTLP probe last attempted contact. Null when no probe has run yet.
    /// </summary>
    public DateTimeOffset? OtlpLastProbedAt { get; init; }
}

/// <summary>
/// Realtime admin capability exposed to observability clients.
/// </summary>
internal sealed class ObservabilityRealtimeStatus
{
    /// <summary>
    /// Whether admin realtime status is supported by this server.
    /// </summary>
    public bool Supported { get; init; }

    /// <summary>
    /// SignalR hub path for admin realtime status.
    /// </summary>
    public string? HubPath { get; init; }

    /// <summary>
    /// Realtime protocol used by the hub.
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Event names currently emitted by the hub.
    /// </summary>
    public string[] Events { get; init; } = [];
}
