// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

/// <summary>The result of admitting a layer publication request.</summary>
public enum LayerPublishAdmissionKind
{
    /// <summary>The layer was published synchronously.</summary>
    Completed,
    /// <summary>The publication was accepted for background execution.</summary>
    Queued,
    /// <summary>No compatible background runner is available.</summary>
    Unavailable,
}

/// <summary>A completed publication or its background job handle.</summary>
/// <param name="Kind">The admission outcome.</param>
/// <param name="Layer">The completed layer, when available.</param>
/// <param name="JobId">The accepted background job identifier.</param>
/// <param name="RowsTotal">The observed source row count.</param>
/// <param name="StatusUrl">The polling URL for an accepted job.</param>
public sealed record LayerPublishAdmission(
    LayerPublishAdmissionKind Kind,
    PublishedLayerSummary? Layer,
    string? JobId,
    long RowsTotal,
    string? StatusUrl);
