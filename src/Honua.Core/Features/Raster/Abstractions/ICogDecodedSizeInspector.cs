// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Bounds the projected <em>decoded</em> grid size of a registered COG by probing only its
/// TIFF/BigTIFF header and first IFD — never its tile-offset arrays or pixel content —
/// within fixed byte/allocation caps.
///
/// <para>
/// The whole-COG geoprocessing raster-source path (#2264) reads a registered COG and
/// base64-encodes the compressed bytes into the analysis plan the worker later decodes.
/// The existing artifact ceiling bounds the <em>compressed</em> byte count, but a tiny
/// compressed TIFF can declare an enormous decoded grid (a decompression bomb) that only
/// materializes when the worker decodes it, driving worker OOM/DoS (RAST-005 / #3090).
/// This inspector computes <c>width * height * bands * ceil(bits/8)</c> from the header
/// alone and lets a submit-time caller fail closed before any bytes are materialized.
/// </para>
/// </summary>
public interface ICogDecodedSizeInspector
{
    /// <summary>
    /// Probes the COG at <paramref name="bucket"/>/<paramref name="key"/> and reports whether
    /// its projected decoded size is within <paramref name="maxDecodedBytes"/>. Reads only the
    /// header and first IFD (plus, at most, the first BitsPerSample element) within fixed caps
    /// and <b>fails closed</b> — returning <see cref="CogDecodedSizeInspection.Accepted"/> =
    /// <see langword="false"/> — whenever the header fields cannot be read within those caps or
    /// the projected size exceeds the ceiling. Never reads tile-offset arrays or pixel data.
    /// </summary>
    Task<CogDecodedSizeInspection> InspectAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        long maxDecodedBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a <see cref="ICogDecodedSizeInspector.InspectAsync"/> probe.
/// </summary>
/// <param name="Accepted">
/// Whether the projected decoded size is within the configured ceiling and every probed
/// header field was read within the caps. <see langword="false"/> means fail-closed reject.
/// </param>
/// <param name="Width">Declared base-image width in pixels (0 when it could not be read).</param>
/// <param name="Height">Declared base-image height in pixels (0 when it could not be read).</param>
/// <param name="BandCount">Declared samples per pixel (clamped to at least 1).</param>
/// <param name="BitsPerSample">Declared bits per sample of the first band (clamped to at least 1).</param>
/// <param name="ProjectedDecodedBytes">
/// <c>width * height * bands * ceil(bits/8)</c>, saturated to <see cref="long.MaxValue"/> on
/// overflow so a crafted grid can never wrap to a small value.
/// </param>
/// <param name="RejectionReason">A caller-safe explanation when <paramref name="Accepted"/> is false.</param>
public readonly record struct CogDecodedSizeInspection(
    bool Accepted,
    long Width,
    long Height,
    int BandCount,
    int BitsPerSample,
    long ProjectedDecodedBytes,
    string? RejectionReason);
