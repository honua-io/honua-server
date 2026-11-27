// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

internal static class WkbFormatReader
{
    private const int CopyBufferSize = 64 * 1024;

    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, ImportLimits.Default, cancellationToken);

    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        ImportLimits? limits,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (maxMemoryBytes, maxWkbSize) = ResolveLimits(limits);
        using var buffer = await CopyBoundedAsync(stream, maxMemoryBytes, cancellationToken).ConfigureAwait(false);
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
            var (geometry, progressed) = ReadNextGeometry(buffer, reader, maxWkbSize);
            if (!progressed)
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

    private static (long MaxMemoryBytes, long MaxWkbSize) ResolveLimits(ImportLimits? limits)
    {
        var effective = limits ?? ImportLimits.Default;
        var maxMemoryBytes = effective.MaxMemoryBytes > 0 ? effective.MaxMemoryBytes : ImportLimits.Default.MaxMemoryBytes;
        var maxWkbSize = effective.MaxWkbSize > 0 ? effective.MaxWkbSize : ImportLimits.Default.MaxWkbSize;
        return (maxMemoryBytes, maxWkbSize);
    }

    private static (NtsGeometry? Geometry, bool Progressed) ReadNextGeometry(
        NonClosingMemoryStream buffer,
        WKBReader reader,
        long maxWkbSize)
    {
        var positionBefore = buffer.Position;
        NtsGeometry geometry;
        try
        {
            geometry = reader.Read(buffer);
        }
        catch (Exception ex) when (ex is ParseException or FormatException or ArgumentException or EndOfStreamException or System.IO.IOException)
        {
            throw new InvalidDataException(
                "WKB payload could not be parsed. Expected one or more concatenated WKB/EWKB geometries.",
                ex);
        }

        var geometryBytes = buffer.Position - positionBefore;
        if (geometryBytes <= 0)
        {
            return (null, false);
        }

        if (geometryBytes > maxWkbSize)
        {
            throw new InvalidDataException(
                $"WKB geometry size ({geometryBytes:N0} bytes) exceeds maximum allowed ({maxWkbSize:N0} bytes).");
        }

        return (geometry, true);
    }

    private static async Task<NonClosingMemoryStream> CopyBoundedAsync(
        Stream stream,
        long maxMemoryBytes,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length - stream.Position > maxMemoryBytes)
        {
            throw new InvalidDataException(
                $"WKB payload exceeds maximum buffered size of {maxMemoryBytes:N0} bytes.");
        }

        var buffer = new NonClosingMemoryStream();
        var chunk = new byte[CopyBufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            if (read > maxMemoryBytes - buffer.Length)
            {
                throw new InvalidDataException(
                    $"WKB payload exceeds maximum buffered size of {maxMemoryBytes:N0} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer;
    }

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
