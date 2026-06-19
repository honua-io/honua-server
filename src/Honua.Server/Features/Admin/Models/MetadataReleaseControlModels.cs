// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request to create and submit an additive metadata-release layer-evolution operation.
/// </summary>
public sealed record CreateMetadataReleaseOperationRequest
{
    /// <summary>Release package identifier the lifecycle advances.</summary>
    public string? PackageId { get; init; }

    /// <summary>Target environment whose current graph snapshot is the rollback target.</summary>
    public string? TargetEnvironment { get; init; }

    /// <summary>Desired Git revision, tag, or ref for the metadata release.</summary>
    public string? DesiredRevision { get; init; }

    /// <summary>Semantic identifier of the resource/layer being evolved and smoke-checked.</summary>
    public string? ResourceSemanticId { get; init; }

    /// <summary>Name of the newly added field the smoke stage asserts is present.</summary>
    public string? NewFieldName { get; init; }

    /// <summary>Canonical type of the new field (for example <c>String</c> or <c>Integer</c>).</summary>
    public string? NewFieldType { get; init; }

    /// <summary>Optional ETL/data-populate workload identifier dispatched after the schema change.</summary>
    public string? DataPopulateWorkloadId { get; init; }

    /// <summary>Stable script identifier for the additive change.</summary>
    public string? ScriptId { get; init; }

    /// <summary>Operator reason or change note.</summary>
    public string? Reason { get; init; }

    /// <summary>Idempotency key used to deduplicate submissions.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Correlation id propagated from upstream systems.</summary>
    public string? CorrelationId { get; init; }
}
