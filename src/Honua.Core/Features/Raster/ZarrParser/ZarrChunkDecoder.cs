// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>Validates complete bounded Zarr chunk streams without process-wide compression switches.</summary>
internal static class ZarrChunkDecoder
{
    private const int InflateBlockBytes = 64 * 1024;
    private const int MaxGzipMembers = 4096;

    internal static void Decode(byte[] raw, string? compressor, byte[] destination, CancellationToken cancellationToken)
    {
        if (compressor is null)
        {
            if (raw.Length != destination.Length)
                throw new InvalidDataException("Zarr uncompressed chunk length does not match its declared shape.");
            raw.CopyTo(destination, 0);
            return;
        }
        if (raw.Length == 0)
            throw new InvalidDataException("Zarr compressed chunks must contain a complete encoded stream.");
        try
        {
            var written = 0;
            if (compressor == "zlib")
            {
                var consumed = Inflate(raw, 0, new Inflater(), destination, ref written, cancellationToken);
                if (consumed != raw.Length)
                    throw new InvalidDataException("Zarr zlib chunk contains trailing bytes.");
            }
            else if (compressor == "gzip")
            {
                var position = 0;
                var members = 0;
                var inflater = new Inflater(noHeader: true);
                while (position < raw.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++members > MaxGzipMembers)
                        throw new InvalidOperationException($"Zarr gzip chunk exceeds the {MaxGzipMembers}-member decoding limit.");
                    position = ReadGzipHeader(raw, position, cancellationToken);
                    var memberStart = written;
                    inflater.Reset();
                    position += Inflate(raw, position, inflater, destination, ref written, cancellationToken);
                    RequireAvailable(raw, position, 8);
                    var checksum = new Crc32();
                    UpdateChecksum(checksum, destination, memberStart, written - memberStart, cancellationToken);
                    var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(position, 4));
                    var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(position + 4, 4));
                    if ((uint)checksum.Value != expectedCrc || expectedSize != (uint)(written - memberStart))
                        throw new InvalidDataException("Zarr gzip chunk has an invalid CRC32 or uncompressed size.");
                    position += 8;
                }
            }
            else
            {
                throw new InvalidOperationException($"Zarr compressor '{compressor}' is not supported.");
            }
            if (written != destination.Length)
                throw new InvalidDataException($"Zarr chunk decoded to {written} bytes; expected {destination.Length}.");
        }
        catch (SharpZipBaseException exception)
        {
            throw new InvalidDataException("Zarr chunk has invalid compressed data.", exception);
        }
    }

    private static int Inflate(byte[] raw, int start, Inflater inflater, byte[] destination, ref int written,
        CancellationToken cancellationToken)
    {
        var supplied = start;
        var probe = new byte[1];
        while (!inflater.IsFinished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inflater.IsNeedingDictionary)
                throw new InvalidDataException("Zarr chunks requiring an external compression dictionary are not supported.");
            if (inflater.IsNeedingInput)
            {
                if (supplied == raw.Length)
                    throw new InvalidDataException("Zarr compressed chunk ended before its stream was complete.");
                var count = Math.Min(InflateBlockBytes, raw.Length - supplied);
                inflater.SetInput(raw, supplied, count);
                supplied += count;
            }
            var beforeInput = inflater.TotalIn;
            var remaining = destination.Length - written;
            var emitted = remaining > 0
                ? inflater.Inflate(destination, written, Math.Min(remaining, InflateBlockBytes))
                : inflater.Inflate(probe, 0, 1);
            if (remaining == 0 && emitted > 0)
                throw new InvalidDataException("Zarr chunk decoded beyond its declared shape.");
            written += emitted;
            if (emitted == 0 && !inflater.IsFinished && !inflater.IsNeedingInput &&
                !inflater.IsNeedingDictionary && inflater.TotalIn == beforeInput)
                throw new InvalidDataException("Zarr compressed chunk could not make decoding progress.");
        }
        return checked((int)inflater.TotalIn);
    }

    private static int ReadGzipHeader(byte[] raw, int position, CancellationToken cancellationToken)
    {
        var start = position;
        RequireAvailable(raw, position, 10);
        if (raw[position] != 0x1f || raw[position + 1] != 0x8b || raw[position + 2] != 8)
            throw new InvalidDataException("Zarr gzip chunk has an invalid header or trailing bytes.");
        var flags = raw[position + 3];
        if ((flags & 0xe0) != 0)
            throw new InvalidDataException("Zarr gzip chunk sets reserved header flags.");
        position += 10;
        if ((flags & 4) != 0)
        {
            RequireAvailable(raw, position, 2);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position, 2));
            position += 2;
            RequireAvailable(raw, position, length);
            position += length;
        }
        if ((flags & 8) != 0) position = SkipGzipString(raw, position);
        if ((flags & 16) != 0) position = SkipGzipString(raw, position);
        if ((flags & 2) != 0)
        {
            RequireAvailable(raw, position, 2);
            var checksum = new Crc32();
            UpdateChecksum(checksum, raw, start, position - start, cancellationToken);
            if ((ushort)(checksum.Value & 0xffff) != BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position, 2)))
                throw new InvalidDataException("Zarr gzip chunk has an invalid header CRC16.");
            position += 2;
        }
        return position;
    }

    private static int SkipGzipString(byte[] raw, int position)
    {
        var terminator = raw.AsSpan(position).IndexOf((byte)0);
        if (terminator < 0)
            throw new InvalidDataException("Zarr gzip chunk has an unterminated header field.");
        return position + terminator + 1;
    }

    private static void UpdateChecksum(Crc32 checksum, byte[] buffer, int start, int count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(InflateBlockBytes, count);
            checksum.Update(new ArraySegment<byte>(buffer, start, length));
            start += length;
            count -= length;
        }
    }

    private static void RequireAvailable(byte[] raw, int position, int count)
    {
        if (position < 0 || count > raw.Length - position)
            throw new InvalidDataException("Zarr gzip chunk has a truncated header or footer.");
    }
}
