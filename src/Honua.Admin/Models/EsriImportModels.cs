// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Models;

public sealed record EsriDiscoverRequest
{
    public string? ServiceUrl { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed record EsriDiscoverResponse
{
    public string ServiceUrl { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int? SpatialReferenceWkid { get; init; }
    public int? MaxRecordCount { get; init; }
    public EsriLayerSummary[] Layers { get; init; } = [];
}

public sealed record EsriLayerSummary
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? GeometryType { get; init; }
    public int? FeatureCount { get; init; }
    public bool HasAttachments { get; init; }
}

public sealed record EsriStartImportRequest
{
    public string? ServiceUrl { get; init; }
    public int LayerId { get; init; }
    public string? TableName { get; init; }
    public int? TargetSrid { get; init; }
    public bool? OverwriteExisting { get; init; }
    public string? WhereClause { get; init; }
    public string[]? OutputFields { get; init; }
    public int? BatchSize { get; init; }
    public int? RequestTimeoutSeconds { get; init; }
    public int? MaxRetries { get; init; }
    public bool? AutoPublish { get; init; }
}

public sealed record EsriImportJobResponse
{
    public string JobId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string StatusUrl { get; init; } = string.Empty;
    public string CancelUrl { get; init; } = string.Empty;
}

public sealed record EsriImportJobsResponse
{
    public EsriImportProgress[] Jobs { get; init; } = [];
}

public sealed record EsriImportCancelResponse
{
    public string JobId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record EsriImportProgress
{
    public string JobId { get; init; } = string.Empty;
    public EsriImportStatus Status { get; init; }
    public int FeaturesProcessed { get; init; }
    public int? EstimatedTotalFeatures { get; init; }
    public int BatchesCompleted { get; init; }
    public int? TotalBatches { get; init; }
    public int FailedFeatures { get; init; }
    public string SourceServiceUrl { get; init; } = string.Empty;
    public int SourceLayerId { get; init; }
    public string? SourceLayerName { get; init; }
    public string TableName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? CurrentPhase { get; init; }
}

public enum EsriImportStatus
{
    Queued,
    Discovering,
    RetrievingFeatures,
    CreatingTable,
    InsertingFeatures,
    Publishing,
    Completed,
    Failed,
    Cancelled
}
