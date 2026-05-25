// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Deterministic writer for the v1 3D Tiles <c>tileset.json</c> document.
/// </summary>
/// <remarks>
/// Uses the source-generated <see cref="TilesetJsonContext"/> for AOT-safe
/// serialization. Properties are written in declaration order; the document
/// model contains no dictionaries so the resulting byte sequence is stable
/// across runs given identical input.
/// </remarks>
public static class TilesetDocumentWriter
{
    /// <summary>
    /// Builds an in-memory tileset document from a single-tile generation
    /// result.
    /// </summary>
    /// <param name="boundingRegionDegrees">Bounding region in WGS-84 degrees [west, south, east, north].</param>
    /// <param name="minHeightMeters">Minimum height across geometry, in meters.</param>
    /// <param name="maxHeightMeters">Maximum height across geometry, in meters.</param>
    /// <param name="geometricError">Geometric error in meters.</param>
    /// <param name="tileContentUris">Ordered relative URIs of child tile content (e.g. <c>tile_0000.glb</c>).</param>
    /// <param name="generatorTag">Optional generator label written into the asset block.</param>
    /// <param name="styleReference">
    /// Optional reference to the emitted style-metadata contract sidecar. When
    /// supplied it is advertised under the root <c>extras.honua_style</c> block
    /// so a client can discover the attribute-driven symbology spec.
    /// </param>
    public static TilesetDocument Build(
        double[] boundingRegionDegrees,
        double minHeightMeters,
        double maxHeightMeters,
        double geometricError,
        IReadOnlyList<string> tileContentUris,
        string? generatorTag = null,
        TilesetStyleReference? styleReference = null)
    {
        ArgumentNullException.ThrowIfNull(boundingRegionDegrees);
        ArgumentNullException.ThrowIfNull(tileContentUris);
        if (boundingRegionDegrees.Length != 4)
        {
            throw new ArgumentException(
                "boundingRegionDegrees must have exactly 4 elements.",
                nameof(boundingRegionDegrees));
        }

        var region = new[]
        {
            boundingRegionDegrees[0] * Math.PI / 180.0,
            boundingRegionDegrees[1] * Math.PI / 180.0,
            boundingRegionDegrees[2] * Math.PI / 180.0,
            boundingRegionDegrees[3] * Math.PI / 180.0,
            minHeightMeters,
            maxHeightMeters
        };

        var children = new List<TileNode>(tileContentUris.Count);
        for (var i = 0; i < tileContentUris.Count; i++)
        {
            children.Add(new TileNode
            {
                BoundingVolume = new BoundingVolume { Region = region },
                GeometricError = 0.0,
                Refine = "ADD",
                Content = new TileContent { Uri = tileContentUris[i] }
            });
        }

        return new TilesetDocument
        {
            Asset = new TilesetAsset
            {
                Version = "1.1",
                Generator = string.IsNullOrEmpty(generatorTag) ? null : generatorTag
            },
            GeometricError = geometricError,
            Root = new TileNode
            {
                BoundingVolume = new BoundingVolume { Region = region },
                GeometricError = geometricError,
                Refine = "ADD",
                Children = children
            },
            Extras = styleReference is null
                ? null
                : new TilesetExtras { Style = styleReference }
        };
    }

    /// <summary>
    /// Serializes the tileset document to a deterministic UTF-8 byte sequence.
    /// </summary>
    public static byte[] Serialize(TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.SerializeToUtf8Bytes(document, TilesetJsonContext.Default.TilesetDocument);
    }
}
