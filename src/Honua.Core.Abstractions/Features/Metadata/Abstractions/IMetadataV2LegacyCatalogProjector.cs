// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Synthesizes a canonical Metadata v2 graph from the legacy V1 catalog
/// (<c>honua.services</c> / <c>honua.layers</c> / <c>honua.service_layers</c> /
/// <c>honua.layer_fields</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the projection seam used by compat seeding paths that write the legacy
/// catalog directly with raw SQL (for example the cloud-demo reset/startup seeder)
/// rather than going through the canonical layer-publishing pipeline. Those paths can
/// build a graph here and persist it through <see cref="IMetadataV2GraphStore.SaveAsync"/>
/// so the freshly-seeded services become visible to every read path that resolves
/// services and collections from the Metadata v2 graph (FeatureServer query, OGC API
/// Features items, etc.). Without this projection the legacy rows exist but the v2 read
/// paths return 404. (honua-server#2081.)
/// </para>
/// <para>
/// The projection is read-only with respect to the Metadata v2 tables: it returns the
/// graph but does not activate it. Callers persist it explicitly so the graph store stays
/// the single owner of snapshot revisions and etags.
/// </para>
/// </remarks>
public interface IMetadataV2LegacyCatalogProjector
{
    /// <summary>
    /// Builds a Metadata v2 graph from the current legacy V1 catalog.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The synthesized graph, or <see langword="null"/> when the legacy catalog has no
    /// published service layers (a truly empty catalog) or the legacy tables are absent.
    /// </returns>
    Task<MetadataV2Graph?> BuildFromLegacyCatalogAsync(CancellationToken cancellationToken = default);
}
