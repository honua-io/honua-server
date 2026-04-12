// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    /// Whether tracing is enabled.
    /// </summary>
    public bool TracingEnabled { get; init; }

    /// <summary>
    /// Whether an OTLP endpoint is configured.
    /// </summary>
    public bool OtlpConfigured { get; init; }

    /// <summary>
    /// Configured OTLP endpoint (if any).
    /// </summary>
    public string? OtlpEndpoint { get; init; }
}
