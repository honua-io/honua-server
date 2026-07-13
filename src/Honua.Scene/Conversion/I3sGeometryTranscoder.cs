// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Core.Features.Scene.Conversion;

/// <summary>
/// Transcodes Honua scene geometry (the same <see cref="SceneFeature"/> source
/// the 3D Tiles <see cref="GeometryTileBuilder"/> consumes) into an Esri I3S
/// <c>nodes/{id}/geometries/0</c> binary buffer (OGC 19-008 Indexed Scene
/// Layers, "Default" geometry, uncompressed interleaved layout). This is the
/// first concrete glTF/3D-Tiles → I3S geometry slice (#1810): it lets an ArcGIS
/// SceneLayer / I3S client actually render a hosted Honua scene rather than only
/// discover its descriptor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> The emitted buffer matches the layer descriptor's advertised
/// <c>geometryDefinitions[0].geometryBuffers[0]</c>: an 8-byte header
/// (<c>vertexCount</c> + <c>featureCount</c>, both little-endian UInt32)
/// followed by an interleaved per-vertex stream
/// (<c>position</c> Float32×3, <c>normal</c> Float32×3, <c>uv0</c> Float32×2,
/// <c>color</c> UInt8×4 = 36 bytes/vertex) and a trailing feature section
/// (per-feature <c>id</c> UInt64 + the half-open vertex <c>[start, count)</c>
/// range that maps each triangle vertex back to its source feature, so identify
/// flows can attribute a picked vertex to a batch id). Triangles are emitted in
/// the deterministic source-feature / fan-triangulation order, so the output is
/// byte-identical across runs for identical input.
/// </para>
/// <para>
/// <b>vertexCRS + per-node MBS offset.</b> Positions are ECEF (EPSG:4978) metres
/// recentred about the node's Minimum Bounding Sphere (MBS) centre — the same
/// relative-to-centre (RTC) scheme the GLB path uses — so the Float32 stream
/// keeps sub-centimetre precision instead of quantising absolute ~6.3e6 m ECEF
/// to ~1 m. The absolute MBS centre is returned in
/// <see cref="I3sTranscodedGeometry.MbsCenterEcef"/> so the node descriptor /
/// node page can publish it; the client reconstructs absolute ECEF as
/// <c>mbsCenter + position</c>. <c>normalReferenceFrame</c> is the I3S default
/// <c>earth-centered</c> (normals are unit ECEF vectors, not east-north-up).
/// </para>
/// <para>
/// <b>Scope (#1810 increment).</b> This slice transcodes the <b>3D Object
/// polygon / extruded-prism</b> geometry profile (triangles) — the profile the
/// hosted Honua building/scene pipeline produces and the one the descriptor
/// advertises. Point and line scene kinds, Draco compression, real UV unwrapping
/// (UVs are emitted as zero placeholders), textures binary serving, and re-parsing
/// an already-baked GLB back into I3S are deliberately deferred; see the ticket.
/// The transcoder is a <b>pure function</b> with no I/O so it is equally usable
/// from a bake-time pipeline or the per-request serving path; this increment
/// wires the simpler per-request path for the single whole-scene node.
/// </para>
/// </remarks>
public static class I3sGeometryTranscoder
{
    /// <summary>Bytes per interleaved vertex: position(12) + normal(12) + uv0(8) + color(4).</summary>
    public const int VertexStrideBytes = 36;

    /// <summary>Header bytes: vertexCount(4) + featureCount(4), little-endian UInt32.</summary>
    public const int HeaderBytes = 8;

    /// <summary>Bytes per feature in the trailing feature section: id(8) + vertexStart(4) + vertexCount(4).</summary>
    public const int FeatureRecordBytes = 16;

    private const byte OpaqueWhiteR = 255;
    private const byte OpaqueWhiteG = 255;
    private const byte OpaqueWhiteB = 255;
    private const byte OpaqueWhiteA = 255;

    /// <summary>
    /// Transcodes the supplied triangle-producing scene features into an I3S
    /// Default geometry buffer for a single node.
    /// </summary>
    /// <param name="features">
    /// Ordered features. All must share <see cref="SceneGeometryKind.Polygon"/>
    /// (flat or extruded); a tile of mixed/point/line kinds is rejected because
    /// the I3S Default 3D Object geometry is triangle-only.
    /// </param>
    /// <param name="extrusion">
    /// Optional extrusion that turns polygons into vertical prisms (top, bottom,
    /// and wall triangles), matching the GLB path's extrusion handling.
    /// </param>
    /// <returns>
    /// The transcoded geometry: the binary buffer plus the node's MBS centre and
    /// radius (absolute ECEF metres) the position stream is relative to.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="features"/> is empty, carries a non-polygon kind, or
    /// produces no renderable triangle vertices.
    /// </exception>
    public static I3sTranscodedGeometry Transcode(
        IReadOnlyList<SceneFeature> features,
        MetadataV2ExtrusionInfo? extrusion = null)
    {
        ArgumentNullException.ThrowIfNull(features);

        if (features.Count == 0)
        {
            throw new ArgumentException("At least one feature is required to transcode a node geometry.", nameof(features));
        }

        for (var i = 0; i < features.Count; i++)
        {
            if (features[i].Geometry.Kind != SceneGeometryKind.Polygon)
            {
                throw new ArgumentException(
                    "The I3S Default 3D Object geometry profile transcodes polygon/extruded features only; " +
                    $"feature at index {i} is '{features[i].Geometry.Kind}'.",
                    nameof(features));
            }
        }

        // 1. Accumulate triangle vertices in double-precision ECEF, tracking the
        //    per-feature half-open vertex range so the feature section can map a
        //    picked vertex back to its source object id.
        var positions = new List<double>(features.Count * 18);
        var featureRanges = new List<(long Id, int Start, int Count)>(features.Count);

        foreach (var feature in features)
        {
            var start = positions.Count / 3;
            AppendFeatureTriangles(feature, extrusion, positions);
            var count = positions.Count / 3 - start;
            featureRanges.Add((feature.Id, start, count));
        }

        var vertexCount = positions.Count / 3;
        if (vertexCount == 0)
        {
            throw new ArgumentException(
                $"No renderable triangle vertices produced for {features.Count} feature(s): all rings are degenerate " +
                "(fewer than 3 distinct vertices). Callers should drop such nodes before transcoding.",
                nameof(features));
        }

        // 2. Compute the node MBS centre (arithmetic mean of vertices) and radius
        //    (max distance from centre). Recentring about the MBS centre BEFORE
        //    the Float32 cast preserves precision (relative-to-centre).
        var (cx, cy, cz) = ComputeCentroid(positions, vertexCount);
        var radius = ComputeRadius(positions, cx, cy, cz);

        // 3. Per-triangle face normals (unit ECEF vectors) shared by the three
        //    vertices of each triangle. normalReferenceFrame = earth-centered.
        var normals = ComputeFaceNormals(positions);

        // 4. Pack the interleaved buffer + feature section.
        var buffer = PackBuffer(positions, normals, featureRanges, vertexCount, cx, cy, cz);

        return new I3sTranscodedGeometry(buffer, [cx, cy, cz], radius, vertexCount, features.Count);
    }

    private static void AppendFeatureTriangles(
        SceneFeature feature,
        MetadataV2ExtrusionInfo? extrusion,
        List<double> positions)
    {
        var ring = feature.Geometry.Vertices;
        if (ring.Count < 3)
        {
            return;
        }

        var ringCount = ring.Count;
        // Drop a closing duplicate vertex (first == last, within tolerance) if present.
        if (SceneVertexCoordinates.IsRingClosingDuplicate(ring[0], ring[ringCount - 1]))
        {
            ringCount--;
        }
        if (ringCount < 3)
        {
            return;
        }

        if (extrusion is null)
        {
            // Flat polygon: fan-triangulate the top face only.
            for (var i = 1; i < ringCount - 1; i++)
            {
                AppendVertex(ring[0], null, positions);
                AppendVertex(ring[i], null, positions);
                AppendVertex(ring[i + 1], null, positions);
            }
            return;
        }

        // Extruded prism: top face, bottom face (reversed), and walls — mirrors
        // GeometryTileBuilder.AppendPolygonTriangles so the I3S and GLB paths
        // tessellate identically.
        var baseHeight = SceneExtrusionResolver.ResolveBaseHeightMeters(feature, extrusion);
        var topZ = baseHeight + SceneExtrusionResolver.ResolveTopHeightMeters(feature, extrusion);

        for (var i = 1; i < ringCount - 1; i++)
        {
            AppendVertex(ring[0], topZ, positions);
            AppendVertex(ring[i], topZ, positions);
            AppendVertex(ring[i + 1], topZ, positions);
        }
        for (var i = 1; i < ringCount - 1; i++)
        {
            AppendVertex(ring[0], baseHeight, positions);
            AppendVertex(ring[i + 1], baseHeight, positions);
            AppendVertex(ring[i], baseHeight, positions);
        }
        for (var i = 0; i < ringCount; i++)
        {
            var current = ring[i];
            var next = ring[(i + 1) % ringCount];
            AppendVertex(current, baseHeight, positions);
            AppendVertex(next, baseHeight, positions);
            AppendVertex(next, topZ, positions);

            AppendVertex(current, baseHeight, positions);
            AppendVertex(next, topZ, positions);
            AppendVertex(current, topZ, positions);
        }
    }

    private static void AppendVertex(SceneVertex vertex, double? heightOverride, List<double> positions)
    {
        var height = heightOverride ?? vertex.Height ?? 0.0;
        var (x, y, z) = EcefCoordinateTransform.ToEcef(vertex.Longitude, vertex.Latitude, height);
        positions.Add(x);
        positions.Add(y);
        positions.Add(z);
    }

    private static (double X, double Y, double Z) ComputeCentroid(List<double> positions, int vertexCount)
    {
        double cx = 0, cy = 0, cz = 0;
        for (var i = 0; i < positions.Count; i += 3)
        {
            cx += positions[i];
            cy += positions[i + 1];
            cz += positions[i + 2];
        }
        return (cx / vertexCount, cy / vertexCount, cz / vertexCount);
    }

    private static double ComputeRadius(List<double> positions, double cx, double cy, double cz)
    {
        var maxSq = 0.0;
        for (var i = 0; i < positions.Count; i += 3)
        {
            var dx = positions[i] - cx;
            var dy = positions[i + 1] - cy;
            var dz = positions[i + 2] - cz;
            var sq = (dx * dx) + (dy * dy) + (dz * dz);
            if (sq > maxSq)
            {
                maxSq = sq;
            }
        }
        return Math.Sqrt(maxSq);
    }

    /// <summary>
    /// Computes one unit ECEF face normal per triangle (the normalised cross
    /// product of two edges) and replicates it across the triangle's three
    /// vertices. Degenerate triangles (collinear vertices) fall back to a unit
    /// radial normal so the stream never carries NaN.
    /// </summary>
    private static float[] ComputeFaceNormals(List<double> positions)
    {
        var vertexCount = positions.Count / 3;
        var normals = new float[vertexCount * 3];
        for (var tri = 0; tri < vertexCount; tri += 3)
        {
            var i0 = tri * 3;
            var i1 = (tri + 1) * 3;
            var i2 = (tri + 2) * 3;

            var ax = positions[i1] - positions[i0];
            var ay = positions[i1 + 1] - positions[i0 + 1];
            var az = positions[i1 + 2] - positions[i0 + 2];
            var bx = positions[i2] - positions[i0];
            var by = positions[i2 + 1] - positions[i0 + 1];
            var bz = positions[i2 + 2] - positions[i0 + 2];

            var nx = (ay * bz) - (az * by);
            var ny = (az * bx) - (ax * bz);
            var nz = (ax * by) - (ay * bx);
            var length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));

            if (length < 1e-12)
            {
                // Degenerate triangle: fall back to the outward radial direction
                // at the first vertex so the normal is a unit vector, not NaN.
                var px = positions[i0];
                var py = positions[i0 + 1];
                var pz = positions[i0 + 2];
                var plen = Math.Sqrt((px * px) + (py * py) + (pz * pz));
                if (plen < 1e-12)
                {
                    nx = 0; ny = 0; nz = 1; length = 1;
                }
                else
                {
                    nx = px; ny = py; nz = pz; length = plen;
                }
            }

            var fnx = (float)(nx / length);
            var fny = (float)(ny / length);
            var fnz = (float)(nz / length);

            for (var v = 0; v < 3; v++)
            {
                var o = (tri + v) * 3;
                normals[o] = fnx;
                normals[o + 1] = fny;
                normals[o + 2] = fnz;
            }
        }
        return normals;
    }

    private static byte[] PackBuffer(
        List<double> positions,
        float[] normals,
        List<(long Id, int Start, int Count)> featureRanges,
        int vertexCount,
        double cx,
        double cy,
        double cz)
    {
        var total = HeaderBytes
            + (vertexCount * VertexStrideBytes)
            + (featureRanges.Count * FeatureRecordBytes);
        var buffer = new byte[total];
        var span = buffer.AsSpan();

        // Header: vertexCount, featureCount.
        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], (uint)vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), (uint)featureRanges.Count);

        // Interleaved per-vertex stream.
        var offset = HeaderBytes;
        for (var v = 0; v < vertexCount; v++)
        {
            var p = v * 3;
            // position (relative to MBS centre)
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset, 4), (float)(positions[p] - cx));
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), (float)(positions[p + 1] - cy));
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), (float)(positions[p + 2] - cz));
            // normal (unit ECEF)
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 12, 4), normals[p]);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 16, 4), normals[p + 1]);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 20, 4), normals[p + 2]);
            // uv0 placeholder (0,0): real unwrapping deferred (#1810 follow-up).
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 24, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 28, 4), 0f);
            // color (opaque white): per-feature symbology baking deferred.
            span[offset + 32] = OpaqueWhiteR;
            span[offset + 33] = OpaqueWhiteG;
            span[offset + 34] = OpaqueWhiteB;
            span[offset + 35] = OpaqueWhiteA;
            offset += VertexStrideBytes;
        }

        // Feature section: id, vertexStart, vertexCount per source feature.
        foreach (var (id, start, count) in featureRanges)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset, 8), unchecked((ulong)id));
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 8, 4), (uint)start);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 12, 4), (uint)count);
            offset += FeatureRecordBytes;
        }

        return buffer;
    }
}
