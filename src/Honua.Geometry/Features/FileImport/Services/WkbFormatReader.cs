// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Reader for Well-Known Binary (WKB/EWKB) geometry payloads. A <c>.wkb</c> upload is one or more
/// concatenated WKB geometries; each is read into a geometry-only feature (WKB carries no
/// attributes). This lets WKB auto-detect and import instead of failing with a generic
/// "Multipart import request is invalid." message (honua-server#2353).
/// </summary>
internal static class WkbFormatReader
{
    /// <summary>
    /// Streams geometry-only features parsed from a stream of concatenated WKB/EWKB geometries.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // NTS WKBReader.Read(Stream) reads exactly one geometry and leaves the stream positioned
        // immediately after it. Buffer to a seekable MemoryStream so we can loop by byte position
        // (import sources are size-capped upstream) and reliably detect end-of-input.
        //
        // WKBReader.Read wraps the stream in a BinaryReader and disposes it, which would otherwise
        // close our buffer after the first geometry and make the next Position read throw
        // ObjectDisposedException. Use a buffer whose Dispose/Close are no-ops so it survives the
        // concatenated-geometry loop; the underlying managed byte array is reclaimed by the GC.
        using var buffer = new NonClosingMemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            yield break;
        }

        buffer.Position = 0;
        var reader = new WKBReader { HandleSRID = true };
        var recordIndex = 0;

        while (buffer.Position < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NtsGeometry geometry;
            var positionBefore = buffer.Position;
            try
            {
                geometry = reader.Read(buffer);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or EndOfStreamException or System.IO.IOException)
            {
                throw new InvalidDataException(
                    "WKB payload could not be parsed. Expected one or more concatenated WKB/EWKB geometries.",
                    ex);
            }

            // Guard against a reader that makes no progress (defensive; avoids an infinite loop
            // on a malformed trailer).
            if (buffer.Position <= positionBefore)
            {
                break;
            }

            if (geometry != null)
            {
                yield return new Feature(geometry, new AttributesTable());
            }

            if (++recordIndex % 256 == 0)
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// A seekable in-memory buffer whose <see cref="Dispose(bool)"/> / <see cref="Close"/> are
    /// no-ops. WKBReader.Read wraps the passed stream in a BinaryReader and disposes it after each
    /// geometry; without this the buffer would close between concatenated geometries and the loop's
    /// Position read would throw. The managed byte array is reclaimed by the GC once unreferenced.
    /// </summary>
    private sealed class NonClosingMemoryStream : MemoryStream
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage",
            "CA2215:Dispose methods should call base class dispose",
            Justification = "Intentional: not disposing keeps the buffer usable across concatenated "
                + "reads; WKBReader.Read's BinaryReader must not close the shared buffer. The managed "
                + "byte array is reclaimed by the GC.")]
        protected override void Dispose(bool disposing)
        {
            // Intentionally do not dispose: keep the buffer usable across concatenated reads.
        }

        public override void Close()
        {
            // Intentionally no-op: BinaryReader.Dispose must not close the shared buffer.
        }
    }
}
