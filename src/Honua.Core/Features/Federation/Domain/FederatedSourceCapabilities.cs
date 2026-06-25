// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Federation.Domain;

/// <summary>
/// Describes which canonical query predicates a federated source can evaluate remotely
/// (push-down) versus which the federation layer must evaluate locally after fetching
/// candidate rows. Capabilities are a pure function of the <see cref="FederatedSourceKind"/>
/// and are resolved offline, so query planning never contacts the remote source.
/// </summary>
public sealed record FederatedSourceCapabilities
{
    /// <summary>
    /// Gets a value indicating whether attribute (<c>WHERE</c> / CQL2) predicates push down.
    /// </summary>
    public required bool SupportsAttributeFilterPushdown { get; init; }

    /// <summary>
    /// Gets a value indicating whether spatial (bbox / envelope) predicates push down.
    /// </summary>
    public required bool SupportsSpatialFilterPushdown { get; init; }

    /// <summary>
    /// Gets a value indicating whether spatial relationships beyond a simple envelope
    /// intersection (for example <c>Within</c>, <c>Crosses</c>, distance, nearest-neighbor)
    /// push down. When <see langword="false"/> the federation layer fetches an envelope
    /// superset and refines the exact relationship locally.
    /// </summary>
    public required bool SupportsExactSpatialRelationshipPushdown { get; init; }

    /// <summary>
    /// Gets a value indicating whether temporal interval predicates push down.
    /// </summary>
    public required bool SupportsTemporalFilterPushdown { get; init; }

    /// <summary>
    /// Gets a value indicating whether ordering (<c>ORDER BY</c>) pushes down. When
    /// <see langword="false"/> the federation layer applies the sort locally.
    /// </summary>
    public required bool SupportsOrderByPushdown { get; init; }

    /// <summary>
    /// Gets a value indicating whether result paging (offset/limit) pushes down. When
    /// <see langword="false"/> the federation layer must over-fetch and page locally.
    /// </summary>
    public required bool SupportsPagingPushdown { get; init; }
}
