// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Factory for the GeoJSON reader/writer pair used at every geoprocessing
/// artifact boundary (inline FeatureCollection data URIs, NDJSON spill streams,
/// and single-Feature geometry artifacts). Both are created with
/// <c>Dimension = 3</c>: the default NetTopologySuite reader/writer are 2D-only
/// and silently truncate Z on serialization, which re-flattened geometries that
/// the executors had carefully preserved (#2744). A dimension-3 writer emits the
/// third ordinate only when it is present and finite, and a dimension-3 reader
/// accepts both 2- and 3-element coordinate arrays, so 2D pipelines are
/// byte-for-byte unaffected. GeoJSON has no M representation, so M survives the
/// in-memory executors but not this envelope.
/// </summary>
internal static class GeoJsonArtifactCodec
{
    /// <summary>
    /// Creates a GeoJSON reader that parses an optional third (Z) ordinate.
    /// Matches the parameterless <see cref="GeoJsonReader"/> in every other
    /// respect (WGS 84 factory, default serializer settings).
    /// </summary>
    public static GeoJsonReader CreateReader()
        => new(
            NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326),
            new JsonSerializerSettings(),
            dimension: 3);

    /// <summary>
    /// Creates a GeoJSON writer that emits the Z ordinate for coordinates that
    /// carry a finite Z, and plain XY otherwise.
    /// </summary>
    public static GeoJsonWriter CreateWriter()
        => new() { Dimension = 3 };
}
