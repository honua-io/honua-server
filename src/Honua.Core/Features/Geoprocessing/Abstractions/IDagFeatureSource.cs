// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// A first-class DAG source connector that streams features from a remote source
/// into a geoprocessing workflow. Each implementation adapts ONE remote-source
/// family (a Honua catalog layer, an ArcGIS GeoServices FeatureServer, an OGC API
/// Features collection, a WFS feature type, or an external PostGIS table) onto the
/// uniform <see cref="DagSourceFeature"/> stream the source executor encodes as the
/// canonical FeatureCollection artifact.
///
/// Implementations REUSE the existing one-shot <c>Honua.Import</c> readers'
/// pagination/streaming rather than re-implementing transport: the ArcGIS reader
/// wraps the migration <c>ArcGisRestClient</c>; the catalog reader wraps the shared
/// query pipeline (<c>IStreamingFeatureStore</c>/<c>IFeatureReader</c>); the OGC/WFS
/// readers reuse the migration HTTP/paging path; the PostGIS reader reuses the same
/// secure-connection secret handling as <c>sink.external-postgis</c>.
/// </summary>
public interface IDagFeatureSource
{
    /// <summary>
    /// The source process id this reader handles (for example
    /// <c>source.esri-featureserver</c>). Matches the catalog
    /// <c>ProcessDefinition.ProcessId</c> and the dispatcher routing key.
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Streams the features matching <paramref name="request"/> from the remote source,
    /// yielding them lazily as the underlying paginated reader advances.
    /// </summary>
    /// <param name="request">The resolved upstream query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of features.</returns>
    IAsyncEnumerable<DagSourceFeature> ReadAsync(
        DagSourceRequest request,
        CancellationToken cancellationToken = default);
}
