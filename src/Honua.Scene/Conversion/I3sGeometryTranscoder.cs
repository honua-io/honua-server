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
/// Layers, "Default" geometry, uncompressed <c>PerAttributeArray</c> layout). This is the
/// first concrete glTF/3D-Tiles → I3S geometry slice (#1810): it lets an ArcGIS
/// SceneLayer / I3S client actually render a hosted Honua scene rather than only
/// discover its descriptor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> The emitted buffer is the I3S 1.7 <c>PerAttributeArray</c>
/// geometry the layer advertises in <c>store.defaultGeometrySchema</c> and
/// <c>geometryDefinitions[0]</c> (Esri i3s-spec
/// <c>defaultGeometrySchema</c> / <c>geometryBuffer</c>): an 8-byte header
/// (<c>vertexCount</c> + <c>featureCount</c>, both little-endian UInt32)
/// followed by contiguous attribute arrays in the fixed order
/// <c>position</c> (Float32×3), <c>normal</c> (Float32×3), <c>uv0</c> (Float32×2),
/// <c>color</c> (UInt8×4), then contiguous feature arrays <c>id</c> (UInt64) and
/// <c>faceRange</c> (UInt32×2). <c>faceRange</c> is the inclusive first and last
/// triangle index of that feature (<c>vertexIndex = faceIndex * 3</c> for this
/// un-indexed mesh). Triangles are emitted in the deterministic source-feature /
/// fan-triangulation order, so the output is byte-identical across runs for
/// identical input. A feature that produces no triangles is omitted: an inclusive
/// face range cannot represent an empty span.
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
    /// <summary>Bytes of one position value (Float32×3).</summary>
    public const int PositionBytesPerVertex = 12;

    /// <summary>Bytes of one normal value (Float32×3).</summary>
    public const int NormalBytesPerVertex = 12;

    /// <summary>Bytes of one uv0 value (Float32×2).</summary>
    public const int Uv0BytesPerVertex = 8;

    /// <summary>Bytes of one color value (UInt8×4).</summary>
    public const int ColorBytesPerVertex = 4;

    /// <summary>
    /// Combined per-vertex attribute bytes (position + normal + uv0 + color).
    /// The vertex section is four contiguous arrays, not an interleaved record;
    /// its size is <c>vertexCount * VertexStrideBytes</c>.
    /// </summary>
    public const int VertexStrideBytes =
        PositionBytesPerVertex + NormalBytesPerVertex + Uv0BytesPerVertex + ColorBytesPerVertex;

    /// <summary>Header bytes: vertexCount(4) + featureCount(4), little-endian UInt32.</summary>
    public const int HeaderBytes = 8;

    /// <summary>Bytes of one feature id (UInt64).</summary>
    public const int FeatureIdBytes = 8;

    /// <summary>Bytes of one faceRange (inclusive first and last triangle index, UInt32×2).</summary>
    public const int FaceRangeBytes = 8;

    /// <summary>
    /// Combined per-feature attribute bytes (id + faceRange). The feature section
    /// is two contiguous arrays, not an interleaved record; its size is
    /// <c>featureCount * FeatureRecordBytes</c>.
    /// </summary>
    public const int FeatureRecordBytes = FeatureIdBytes + FaceRangeBytes;

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
        //    per-feature vertex span so faceRange can name that feature's
        //    inclusive triangle indices.
        var positions = new List<double>(features.Count * 18);
        var featureRanges = new List<(long Id, int Start, int Count)>(features.Count);

        foreach (var feature in features)
        {
            var start = positions.Count / 3;
            AppendFeatureTriangles(feature, extrusion, positions);
            var count = positions.Count / 3 - start;
            // faceRange is an inclusive triangle pair (I3S geometryFaceRange).
            // A degenerate ring owns no faces, and an inclusive range cannot
            // represent an empty span, so those features are left out.
            if (count == 0)
            {
                continue;
            }

            if ((start % 3) != 0 || (count % 3) != 0)
            {
                throw new InvalidOperationException(
                    $"Feature {feature.Id} produced vertex span [{start}, {count}) " +
                    "which is not a whole number of triangles.");
            }

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

        // 4. Pack contiguous attribute arrays + the feature id / faceRange arrays.
        var buffer = PackBuffer(positions, normals, featureRanges, vertexCount, cx, cy, cz);

        return new I3sTranscodedGeometry(buffer, [cx, cy, cz], radius, vertexCount, featureRanges.Count);
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

        // Contiguous vertex attributes, fixed I3S order: position, normal, uv0, color.
        var positionOffset = HeaderBytes;
        var normalOffset = positionOffset + (vertexCount * PositionBytesPerVertex);
        var uvOffset = normalOffset + (vertexCount * NormalBytesPerVertex);
        var colorOffset = uvOffset + (vertexCount * Uv0BytesPerVertex);
        for (var v = 0; v < vertexCount; v++)
        {
            var p = v * 3;
            var positionAt = positionOffset + (v * PositionBytesPerVertex);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(positionAt, 4), (float)(positions[p] - cx));
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(positionAt + 4, 4), (float)(positions[p + 1] - cy));
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(positionAt + 8, 4), (float)(positions[p + 2] - cz));

            var normalAt = normalOffset + (v * NormalBytesPerVertex);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(normalAt, 4), normals[p]);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(normalAt + 4, 4), normals[p + 1]);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(normalAt + 8, 4), normals[p + 2]);

            // uv0 placeholder (0,0): real unwrapping deferred (#1810 follow-up).
            var uvAt = uvOffset + (v * Uv0BytesPerVertex);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(uvAt, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(uvAt + 4, 4), 0f);

            // color (opaque white): per-feature symbology baking deferred.
            var colorAt = colorOffset + (v * ColorBytesPerVertex);
            span[colorAt] = OpaqueWhiteR;
            span[colorAt + 1] = OpaqueWhiteG;
            span[colorAt + 2] = OpaqueWhiteB;
            span[colorAt + 3] = OpaqueWhiteA;
        }

        // Feature attributes, contiguous: every id, then every faceRange.
        // faceRange is the inclusive [first, last] triangle index
        // (vertexIndex = faceIndex * 3). See I3S geometryFaceRange.
        var featureIdOffset = colorOffset + (vertexCount * ColorBytesPerVertex);
        var faceRangeOffset = featureIdOffset + (featureRanges.Count * FeatureIdBytes);
        for (var i = 0; i < featureRanges.Count; i++)
        {
            var (id, start, count) = featureRanges[i];
            if (count <= 0 || (start % 3) != 0 || (count % 3) != 0)
            {
                throw new InvalidOperationException(
                    $"Feature {id} vertex span [{start}, {count}) is not a positive multiple of three vertices.");
            }

            BinaryPrimitives.WriteUInt64LittleEndian(
                span.Slice(featureIdOffset + (i * FeatureIdBytes), FeatureIdBytes),
                unchecked((ulong)id));

            var firstFace = (uint)(start / 3);
            var lastFace = (uint)((start + count) / 3 - 1);
            var faceAt = faceRangeOffset + (i * FaceRangeBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(faceAt, 4), firstFace);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(faceAt + 4, 4), lastFace);
        }

        return buffer;
    }
}
