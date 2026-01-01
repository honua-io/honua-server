// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.ServiceDefaults;

/// <summary>
/// Configuration options for OpenTelemetry distributed tracing.
/// </summary>
public class TracingOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Tracing";

    /// <summary>
    /// Gets or sets whether tracing is enabled. Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the sampling ratio (0.0 to 1.0).
    /// 1.0 means all traces are sampled (development default).
    /// For production, a lower value like 0.1 (10%) is recommended.
    /// </summary>
    public double SamplingRatio { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets whether to include database query text in spans.
    /// Disable in production for security if queries contain sensitive data.
    /// </summary>
    public bool IncludeDbStatementText { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to trace health check endpoints.
    /// Disable to reduce noise from frequent health checks.
    /// </summary>
    public bool TraceHealthEndpoints { get; set; }

    /// <summary>
    /// Gets or sets whether to record exception stack traces in spans.
    /// </summary>
    public bool RecordExceptionStackTraces { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of attributes per span.
    /// </summary>
    public int MaxAttributesPerSpan { get; set; } = 128;

    /// <summary>
    /// Gets or sets the maximum number of events per span.
    /// </summary>
    public int MaxEventsPerSpan { get; set; } = 128;

    /// <summary>
    /// Gets or sets the OTLP exporter endpoint URL.
    /// When not set, uses the OTEL_EXPORTER_OTLP_ENDPOINT environment variable.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// Gets or sets additional OTLP exporter headers (e.g., for authentication).
    /// Format: "key1=value1,key2=value2"
    /// </summary>
    public string? OtlpHeaders { get; set; }
}
