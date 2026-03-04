// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Proto = Honua.Server.Features.Grpc.Proto.V2;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Service for encoding geometries in multiple formats (WKB, WKT, GeoJSON, ESRI Shape, Structured).
/// Optimized for mobile clients with support for compression and simplification.
/// </summary>
public interface IGeometryEncodingService
{
    /// <summary>
    /// Encodes a geometry in the specified format with optional limits.
    /// </summary>
    /// <param name="geometry">The geometry to encode.</param>
    /// <param name="encoding">The target encoding format.</param>
    /// <param name="limits">Geometry limits for compression and simplification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Encoded geometry proto message.</returns>
    Task<Proto.Geometry> EncodeGeometryAsync(
        GeometryValue geometry,
        Proto.GeometryEncoding encoding,
        GeometryLimits limits,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decodes a geometry from the proto format to internal representation.
    /// </summary>
    /// <param name="protoGeometry">The proto geometry to decode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Internal geometry representation.</returns>
    Task<GeometryValue?> DecodeGeometryAsync(
        Proto.Geometry protoGeometry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the estimated size reduction for different encoding formats.
    /// </summary>
    /// <param name="geometry">The geometry to analyze.</param>
    /// <returns>Size estimates for each encoding format.</returns>
    EncodingSizeEstimate EstimateEncodingSizes(GeometryValue geometry);

    /// <summary>
    /// Determines the optimal encoding for a geometry based on size and complexity.
    /// </summary>
    /// <param name="geometry">The geometry to analyze.</param>
    /// <param name="targetPlatform">Target platform (mobile, web, desktop).</param>
    /// <returns>Recommended encoding format.</returns>
    Proto.GeometryEncoding GetOptimalEncoding(GeometryValue geometry, string targetPlatform);
}

/// <summary>
/// Size estimates for different geometry encoding formats.
/// </summary>
public class EncodingSizeEstimate
{
    public int StructuredBytes { get; init; }
    public int WkbBytes { get; init; }
    public int WktBytes { get; init; }
    public int GeoJsonBytes { get; init; }
    public int EsriShapeBytes { get; init; }

    public Proto.GeometryEncoding SmallestEncoding => GetSmallestEncoding();

    private Proto.GeometryEncoding GetSmallestEncoding()
    {
        var sizes = new Dictionary<Proto.GeometryEncoding, int>
        {
            [Proto.GeometryEncoding.Structured] = StructuredBytes,
            [Proto.GeometryEncoding.Wkb] = WkbBytes,
            [Proto.GeometryEncoding.Wkt] = WktBytes,
            [Proto.GeometryEncoding.Geojson] = GeoJsonBytes,
            [Proto.GeometryEncoding.EsriShape] = EsriShapeBytes
        };

        return sizes.OrderBy(kvp => kvp.Value).First().Key;
    }
}