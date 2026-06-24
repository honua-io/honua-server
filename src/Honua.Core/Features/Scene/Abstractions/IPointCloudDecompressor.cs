// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Scene.Abstractions;

/// <summary>
/// Binds a request principal to an <see cref="IPointCloudDecompressor"/> (#1854).
/// Decompression authorizes and submits the worker job under the calling
/// operator's identity, which is only available per request, so the long-lived
/// dependencies live in the factory and the principal is supplied when the
/// ingest endpoint creates the per-request decompressor. A registered factory
/// signals to the ingest path that out-of-tree LAZ/COPC decompression is
/// available; its absence falls back to rejecting compressed/projected input.
/// </summary>
public interface IPointCloudDecompressorFactory
{
    /// <summary>Creates a decompressor bound to the supplied request principal.</summary>
    /// <param name="principal">The authenticated operator the worker job runs under.</param>
    IPointCloudDecompressor Create(ClaimsPrincipal principal);
}

/// <summary>
/// Decompresses a LAZ/COPC point-cloud buffer (and, when the source is in a
/// projected CRS, reprojects it to geographic EPSG:4979) into the uncompressed
/// LAS the pure-managed scene tiler consumes (#1854).
/// </summary>
/// <remarks>
/// <para>
/// The managed <c>LasPointCloudReader</c> + tiler can only consume uncompressed,
/// geographic LAS. Real-world point clouds are almost always LAZ/COPC and are
/// frequently delivered in a projected CRS, so the admin scene-ingest path
/// detects those cases on upload and dispatches the heavyweight, native
/// decompression/reprojection work out of process via the <c>pcloud.translate</c>
/// process (PDAL, built on laz-perf + PROJ), then tiles the returned LAS inline.
/// </para>
/// <para>
/// This abstraction keeps the <c>Honua.Scene</c> tiling subsystem free of any
/// dependency on the geoprocessing/job runtime: <c>Honua.Scene</c> defines the
/// pure-managed reader and tiler, while the server composition root binds an
/// implementation that submits the canonical process plan and awaits the worker
/// artifact. When no implementation is registered (for example a build without a
/// point-cloud worker), the ingest path falls back to rejecting compressed input
/// as before.
/// </para>
/// </remarks>
public interface IPointCloudDecompressor
{
    /// <summary>
    /// Decompresses <paramref name="source"/> (LAZ or COPC) to uncompressed LAS,
    /// reprojecting to geographic EPSG:4979 when <paramref name="sourceSrs"/>
    /// names a projected CRS. An uncompressed-but-projected source is accepted and
    /// reprojected; a geographic source is decompressed verbatim.
    /// </summary>
    /// <param name="source">The uploaded LAZ/COPC (or projected uncompressed LAS) bytes.</param>
    /// <param name="sourceSrs">
    /// Optional source CRS token (for example <c>EPSG:32610</c> or a bare positive
    /// EPSG integer). When supplied and non-geographic, reprojection to
    /// EPSG:4979 is applied; when omitted or geographic, no reprojection occurs.
    /// </param>
    /// <param name="cancellationToken">Cancels the dispatch and any polling.</param>
    /// <returns>The decompressed (and, where applicable, reprojected) uncompressed LAS bytes.</returns>
    /// <exception cref="PointCloudDecompressionException">
    /// The worker rejected the input, failed, timed out, or produced no artifact.
    /// </exception>
    Task<byte[]> DecompressAsync(byte[] source, string? sourceSrs, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when out-of-process point-cloud decompression/reprojection
/// (<c>pcloud.translate</c>) fails. Carries a stable, non-sensitive message the
/// ingest endpoint maps to a problem-detail without leaking worker internals.
/// </summary>
public sealed class PointCloudDecompressionException : Exception
{
    /// <summary>Creates a <see cref="PointCloudDecompressionException"/>.</summary>
    /// <param name="message">A non-sensitive, human-readable failure description.</param>
    public PointCloudDecompressionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a <see cref="PointCloudDecompressionException"/> wrapping an inner cause.</summary>
    /// <param name="message">A non-sensitive, human-readable failure description.</param>
    /// <param name="innerException">The underlying cause.</param>
    public PointCloudDecompressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
