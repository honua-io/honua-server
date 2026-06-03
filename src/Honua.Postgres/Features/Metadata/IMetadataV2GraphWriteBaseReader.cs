// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Postgres.Features.Metadata;

/// <summary>
/// Loads the genuinely-persisted ("activated") Metadata v2 snapshot for an environment
/// without the V1-catalog compatibility synthesis that
/// <see cref="Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider.GetCurrentAsync"/>
/// performs on read paths.
///
/// The compat synthesis (honua-server#1412) exists so serving paths (OGC API Features,
/// WMS/WFS/WCS, GeoServices, OData, STAC) keep serving from the legacy V1 catalog when no
/// v2 snapshot has ever been activated, instead of returning HTTP 500. The admin/publish
/// <em>write</em> path must NOT build on that synthesized graph: it loads the current graph
/// as the base it mutates and saves, and a synthesized base (which is never persisted, so
/// <c>metadata_v2_current</c> has no matching row) causes the publish to fail reconciliation
/// — every <c>AutoPublish</c> import then reports "publishing did not complete". The write
/// path therefore loads only the persisted snapshot and starts from an empty graph on its
/// first publish. (honua-server#1412.)
/// </summary>
internal interface IMetadataV2GraphWriteBaseReader
{
    /// <summary>
    /// Returns the persisted/activated current snapshot for the environment, or
    /// <c>null</c> when no snapshot has been activated (a fresh database). Never
    /// synthesizes a compat snapshot from the V1 catalog.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MetadataV2GraphSnapshot?> TryGetPersistedCurrentAsync(CancellationToken cancellationToken = default);
}
