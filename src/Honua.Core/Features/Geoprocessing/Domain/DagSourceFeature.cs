// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// A single feature streamed from a remote DAG source connector
/// (<c>source.honua-layer</c>, <c>source.esri-featureserver</c>,
/// <c>source.ogc-features</c>, <c>source.wfs</c>, <c>source.postgis</c>).
///
/// The geometry is carried as a GeoJSON geometry document string rather than a
/// NetTopologySuite geometry so the readers stay free of an NTS dependency (they
/// live in the data-provider / core assemblies) while the geoprocessing executor
/// rehydrates the canonical <c>FeatureCollection</c> artifact through the shared
/// managed NetTopologySuite reader. A <see langword="null"/> <see cref="GeometryGeoJson"/>
/// represents a feature with no geometry; attributes still flow through.
/// </summary>
public sealed record DagSourceFeature
{
    /// <summary>
    /// The feature geometry as a GeoJSON geometry object document (for example
    /// <c>{"type":"Point","coordinates":[1,2]}</c>), or <see langword="null"/> when
    /// the source feature has no geometry.
    /// </summary>
    public string? GeometryGeoJson { get; init; }

    /// <summary>
    /// The feature attributes keyed by field name. Values are the raw scalar values
    /// surfaced by the source reader (string/number/bool/null).
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Attributes { get; init; }
}
