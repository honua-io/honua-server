// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed record SupportTelemetryHealthPayload
{
    [JsonPropertyName("readiness")]
    public string? Readiness { get; init; }

    [JsonPropertyName("liveness")]
    public string? Liveness { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("metadataSchemaVersion")]
    public string? MetadataSchemaVersion { get; init; }

    [JsonPropertyName("overallStatus")]
    public string? OverallStatus { get; init; }

    [JsonPropertyName("migrationStatus")]
    public string? MigrationStatus { get; init; }

    [JsonPropertyName("artifactDigest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactDigest { get; init; }

    [JsonPropertyName("platformLockDigest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlatformLockDigest { get; init; }
}

internal sealed record SupportTelemetryErrorPayload
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("statusCode")]
    public int? StatusCode { get; init; }
}

internal sealed record SupportTelemetryMetricsPayload
{
    [JsonPropertyName("httpP95DurationMs")]
    public double? HttpP95DurationMs { get; init; }

    [JsonPropertyName("httpAvgDurationMs")]
    public double? HttpAvgDurationMs { get; init; }

    [JsonPropertyName("httpServerErrorRate")]
    public double? HttpServerErrorRate { get; init; }

    [JsonPropertyName("httpTotalRequests")]
    public long? HttpTotalRequests { get; init; }

    [JsonPropertyName("httpSlowRequests")]
    public long? HttpSlowRequests { get; init; }

    [JsonPropertyName("memoryUsageMB")]
    public double? MemoryUsageMB { get; init; }

    [JsonPropertyName("memoryPressurePercent")]
    public double? MemoryPressurePercent { get; init; }

    [JsonPropertyName("dbCacheHitRate")]
    public double? DbCacheHitRate { get; init; }

    [JsonPropertyName("dbCacheHits")]
    public long? DbCacheHits { get; init; }

    [JsonPropertyName("dbCacheMisses")]
    public long? DbCacheMisses { get; init; }
}

internal sealed record SupportTelemetryOpsFindingPayload
{
    [JsonPropertyName("findingId")]
    public string? FindingId { get; init; }

    [JsonPropertyName("rule")]
    public string? Rule { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("recommendedAction")]
    public string? RecommendedAction { get; init; }

    [JsonPropertyName("detectedAt")]
    public DateTimeOffset? DetectedAt { get; init; }
}

internal sealed record SupportTelemetrySnapshotPayload
{
    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; init; }

    [JsonPropertyName("instanceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstanceUrl { get; init; }

    [JsonPropertyName("health")]
    public SupportTelemetryHealthPayload? Health { get; init; }

    [JsonPropertyName("recentErrors")]
    public IReadOnlyList<SupportTelemetryErrorPayload>? RecentErrors { get; init; }

    [JsonPropertyName("metrics")]
    public SupportTelemetryMetricsPayload? Metrics { get; init; }

    [JsonPropertyName("opsFindings")]
    public IReadOnlyList<SupportTelemetryOpsFindingPayload>? OpsFindings { get; init; }
}

[JsonSerializable(typeof(SupportTelemetrySnapshotPayload))]
internal sealed partial class SupportTelemetryJsonContext : JsonSerializerContext
{
}
