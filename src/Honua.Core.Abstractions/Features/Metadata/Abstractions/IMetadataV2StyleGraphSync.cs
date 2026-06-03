// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Synchronizes the canonical Metadata v2 graph with the independent style catalog
/// (ADR-0048, Phase 2, issue #1389). Called after a style is created, updated, deleted,
/// or (dis)associated from a layer so the graph's <c>Type=Style</c> resources and the
/// data resources' <c>StyleResourceIds</c> reflect the catalog without waiting for a
/// re-publish. Implemented where graph-store write access lives (the provider project),
/// and exposed as a Core abstraction so the server-side style services can invoke it
/// without taking a hard provider dependency.
/// </summary>
public interface IMetadataV2StyleGraphSync
{
    /// <summary>
    /// Reconciles the <c>Type=Style</c> resources and <c>StyleResourceIds</c> for a layer
    /// against the current style-catalog associations for that layer. Adds or refreshes
    /// the layer's style resources, points the data resource's <c>StyleResourceIds</c> at
    /// them (primary first), and prunes any orphaned style resources no longer referenced
    /// by any resource. A no-op when the graph has no resource for the layer.
    /// </summary>
    /// <param name="layerId">Integer storage-layer identifier whose styles changed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SyncLayerStylesAsync(int layerId, CancellationToken cancellationToken = default);
}
