// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Honua.Core.Features.Raster.ZarrParser;

internal readonly record struct ZarrChunkDecodeRequest
{
    public required byte[] Raw { get; init; }

    public string? Compressor { get; init; }

    public required byte[] Destination { get; init; }

    public int DestinationLength { get; init; }
}

internal static class ZarrChunkDecoder
{
    private const int InflateBlockBytes = 64 * 1024;
    private const int MaxGzipMembers = 4096;

    internal static void Decode(ZarrChunkDecodeRequest request, CancellationToken cancellationToken)
    {
        if (request.Compressor is null)
        {
            CopyUncompressed(request);
            return;
        }

        if (request.Raw.Length == 0)
            throw new InvalidDataException("Zarr compressed chunks must contain a complete encoded stream.");

        try
        {
            var written = request.Compressor switch
            {
                "zlib" => DecodeZlib(request, cancellationToken),
                "gzip" => DecodeGzip(request, cancellationToken),
                _ => throw new ZarrSubsetValidationException($"Zarr compressor '{request.Compressor}' is not supported."),
            };
            if (written != request.DestinationLength)
                throw new InvalidDataException($"Zarr chunk decoded to {written} bytes; expected {request.DestinationLength}.");
        }
        catch (SharpZipBaseException exception)
        {
            throw new InvalidDataException("Zarr chunk has invalid compressed data.", exception);
        }
    }

    private static void CopyUncompressed(ZarrChunkDecodeRequest request)
    {
        if (request.Raw.Length != request.DestinationLength)
            throw new InvalidDataException("Zarr uncompressed chunk length does not match its declared shape.");
        Buffer.BlockCopy(request.Raw, 0, request.Destination, 0, request.DestinationLength);
    }

    private static int DecodeZlib(ZarrChunkDecodeRequest request, CancellationToken cancellationToken)
    {
        var result = Inflate(
            new InflateRequest
            {
                Raw = request.Raw,
                Start = 0,
                Inflater = new Inflater(),
                Destination = request.Destination,
                DestinationLength = request.DestinationLength,
                Written = 0,
            },
            cancellationToken);
        if (result.Consumed != request.Raw.Length)
            throw new InvalidDataException("Zarr zlib chunk contains trailing bytes.");
        return result.Written;
    }

    private static int DecodeGzip(ZarrChunkDecodeRequest request, CancellationToken cancellationToken)
    {
        var position = 0;
        var written = 0;
        var members = 0;
        var inflater = new Inflater(noHeader: true);
        while (position < request.Raw.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++members > MaxGzipMembers)
                throw new ZarrSubsetValidationException($"Zarr gzip chunk exceeds the {MaxGzipMembers}-member decoding limit.");
            position = ReadGzipHeader(request.Raw, position, cancellationToken);
            var memberStart = written;
            inflater.Reset();
            var result = Inflate(
                new InflateRequest
                {
                    Raw = request.Raw,
                    Start = position,
                    Inflater = inflater,
                    Destination = request.Destination,
                    DestinationLength = request.DestinationLength,
                    Written = written,
                },
                cancellationToken);
            position += result.Consumed;
            written = result.Written;
            RequireAvailable(request.Raw, position, 8);
            var memberSegment = new ArraySegment<byte>(request.Destination, memberStart, written - memberStart);
            ValidateGzipFooter(request.Raw.AsSpan(position, 8), memberSegment, cancellationToken);
            position += 8;
        }
        return written;
    }

    private static void ValidateGzipFooter(
        ReadOnlySpan<byte> footer,
        ArraySegment<byte> memberSegment,
        CancellationToken cancellationToken)
    {
        var checksum = new Crc32();
        UpdateChecksum(checksum, memberSegment.Array!, memberSegment.Offset, memberSegment.Count, cancellationToken);
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
        var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(4, 4));
        if ((uint)checksum.Value != expectedCrc || expectedSize != (uint)memberSegment.Count)
            throw new InvalidDataException("Zarr gzip chunk has an invalid CRC32 or uncompressed size.");
    }

    private static InflateResult Inflate(InflateRequest request, CancellationToken cancellationToken)
    {
        var supplied = request.Start;
        var written = request.Written;
        var inflater = request.Inflater;
        var probe = new byte[1];
        while (!inflater.IsFinished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inflater.IsNeedingDictionary)
                throw new InvalidDataException("Zarr chunks requiring an external compression dictionary are not supported.");
            if (inflater.IsNeedingInput)
            {
                if (supplied == request.Raw.Length)
                    throw new InvalidDataException("Zarr compressed chunk ended before its stream was complete.");
                var count = Math.Min(InflateBlockBytes, request.Raw.Length - supplied);
                inflater.SetInput(request.Raw, supplied, count);
                supplied += count;
            }
            var beforeInput = inflater.TotalIn;
            var remaining = request.DestinationLength - written;
            var emitted = remaining > 0
                ? inflater.Inflate(request.Destination, written, Math.Min(remaining, InflateBlockBytes))
                : inflater.Inflate(probe, 0, 1);
            if (remaining == 0 && emitted > 0)
                throw new InvalidDataException("Zarr chunk decoded beyond its declared shape.");
            written += emitted;
            if (emitted == 0 && !inflater.IsFinished && !inflater.IsNeedingInput &&
                !inflater.IsNeedingDictionary && inflater.TotalIn == beforeInput)
                throw new InvalidDataException("Zarr compressed chunk could not make decoding progress.");
        }
        return new InflateResult { Consumed = checked((int)inflater.TotalIn), Written = written };
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

    private readonly record struct InflateRequest
    {
        public required byte[] Raw { get; init; }

        public required int Start { get; init; }

        public required Inflater Inflater { get; init; }

        public required byte[] Destination { get; init; }

        public required int DestinationLength { get; init; }

        public required int Written { get; init; }
    }

    private readonly record struct InflateResult
    {
        public required int Consumed { get; init; }

        public required int Written { get; init; }
    }
}
