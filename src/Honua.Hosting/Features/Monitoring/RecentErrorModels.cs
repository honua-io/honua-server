// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Monitoring;

internal sealed class RecentErrorEntry
{
    public DateTimeOffset Timestamp { get; init; }

    public string CorrelationId { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public int StatusCode { get; init; }

    public string Message { get; init; } = string.Empty;
}

internal sealed class RecentErrorsResponse
{
    public int Capacity { get; init; }

    public string InstanceId { get; init; } = string.Empty;

    public IReadOnlyList<RecentErrorEntry> Errors { get; init; } = Array.Empty<RecentErrorEntry>();
}

internal sealed class ObservabilityStatusResponse
{
    public DateTimeOffset GeneratedAt { get; init; }

    public ObservabilityRealtimeStatus Realtime { get; init; } = new();

    public bool TracingEnabled { get; init; }

    public bool MetricsEnabled { get; init; }

    public bool LogsEnabled { get; init; }

    public bool OtlpConfigured { get; init; }

    public bool OtlpEndpointValid { get; init; }

    public string? OtlpEndpoint { get; init; }

    public bool OtlpHeadersConfigured { get; init; }

    public string OtlpExporterState { get; init; } = "notConfigured";

    public string TraceExportState { get; init; } = "notConfigured";

    public string MetricsExportState { get; init; } = "notConfigured";

    public string LogExportState { get; init; } = "notConfigured";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LastExportError { get; init; }

    public string OtlpProbeState { get; init; } = "unknown";

    public string? OtlpLastProbeError { get; init; }

    public DateTimeOffset? OtlpLastProbedAt { get; init; }
}

internal sealed class ObservabilityRealtimeStatus
{
    public bool Supported { get; init; }

    public string? HubPath { get; init; }

    public string? Protocol { get; init; }

    public string[] Events { get; init; } = [];
}
