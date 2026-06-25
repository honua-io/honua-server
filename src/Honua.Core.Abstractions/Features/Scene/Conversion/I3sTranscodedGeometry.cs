// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Conversion;

/// <summary>
/// The result of transcoding scene geometry into an Esri I3S node geometry
/// buffer (#1810): the binary payload plus the Minimum Bounding Sphere (MBS) the
/// position stream is relative to.
/// </summary>
/// <param name="Buffer">
/// The I3S Default geometry binary buffer (header + interleaved vertex stream +
/// feature section). Suitable to serve verbatim at
/// <c>nodes/{id}/geometries/0</c>.
/// </param>
/// <param name="MbsCenterEcef">
/// Absolute ECEF (EPSG:4978) metres centre of the node MBS. The buffer's
/// position stream is relative to this point; absolute ECEF is reconstructed as
/// <c>MbsCenterEcef + position</c>.
/// </param>
/// <param name="MbsRadiusMeters">MBS radius in metres (max vertex distance from the centre).</param>
/// <param name="VertexCount">Total triangle vertices in the buffer (a multiple of 3).</param>
/// <param name="FeatureCount">Number of source features represented in the feature section.</param>
public readonly record struct I3sTranscodedGeometry(
    byte[] Buffer,
    IReadOnlyList<double> MbsCenterEcef,
    double MbsRadiusMeters,
    int VertexCount,
    int FeatureCount);
