// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Cog;

internal static class CogTileDecoder
{
    public static RasterResult? DecodeAndFormat(CogTileDecodeContext context, byte[] tileData, ILogger logger)
    {
        var assembled = AssembleJpegIfNeeded(context, tileData, logger);
        if (assembled is null)
        {
            return null;
        }

        var metadata = context.Metadata;
        var predictor = context.Overview.Predictor > 0 ? context.Overview.Predictor : metadata.Predictor;
        var layout = new TilePixelLayout(metadata.TileWidth, metadata.BandCount, metadata.BitsPerSample, predictor, metadata.IsLittleEndian);
        var (decompressedData, contentType) = TileDecompressor.Decompress(assembled, metadata.Compression, layout);
        if (context.Quadrant is { } quadrant)
        {
            decompressedData = CogTileOverzoom.Upsample(decompressedData, metadata, quadrant);
        }

        (decompressedData, contentType) = EncodeRequestedFormat(decompressedData, contentType, context);
        if (!CanServeRequestedFormat(context.Format, contentType))
        {
            var requestedFormat = FormatName(context.Format);
            CogLog.UnsupportedTileFormat(logger, context.RegistrationId, requestedFormat, contentType);
            return null;
        }

        CogLog.CogTileServed(logger, context.RegistrationId, context.Tile.Z, context.Tile.Y, context.Tile.X, decompressedData.Length, context.MetadataSource);
        return new RasterResult
        {
            Data = decompressedData,
            ContentType = contentType,
            Width = metadata.TileWidth,
            Height = metadata.TileHeight,
            Srid = metadata.Srid,
        };
    }

    private static byte[]? AssembleJpegIfNeeded(CogTileDecodeContext context, byte[] tileData, ILogger logger)
    {
        if (context.Metadata.Compression != "JPEG")
        {
            return tileData;
        }

        var jpeg = JpegTileAssembler.Assemble(tileData, context.Overview.JpegTables, context.Metadata);
        if (jpeg is null)
        {
            CogLog.UndecodableJpegTile(logger, context.RegistrationId, context.Tile.Z, context.Tile.Y, context.Tile.X);
        }

        return jpeg;
    }

    private static (byte[] Data, string ContentType) EncodeRequestedFormat(
        byte[] data,
        string contentType,
        CogTileDecodeContext context)
    {
        if (contentType != "application/octet-stream")
        {
            return (data, contentType);
        }

        if (context.Format == RasterFormat.PNG)
        {
            var png = CogTileEncoder.EncodePng(data, context.Metadata);
            return png is not null ? (png, "image/png") : (data, contentType);
        }

        if (context.Format is RasterFormat.TIFF or RasterFormat.COG)
        {
            var bounds = TileMath.GetTileBounds(context.Tile.X, context.Tile.Y, context.Tile.Z);
            var tiff = CogTiffTileEncoder.Encode(data, context.Metadata, new RasterExtent
            {
                XMin = bounds.XMin,
                YMin = bounds.YMin,
                XMax = bounds.XMax,
                YMax = bounds.YMax,
                Srid = 3857,
            });
            return tiff is not null ? (tiff, "image/tiff") : (data, contentType);
        }

        return (data, contentType);
    }

    private static bool CanServeRequestedFormat(RasterFormat requestedFormat, string contentType) => requestedFormat switch
    {
        RasterFormat.PNG => contentType == "image/png",
        RasterFormat.JPEG => contentType == "image/jpeg",
        RasterFormat.TIFF or RasterFormat.COG => contentType == "image/tiff",
        RasterFormat.Raw => contentType == "application/octet-stream",
        _ => false,
    };

    private static string FormatName(RasterFormat format) => format switch
    {
        RasterFormat.PNG => "PNG",
        RasterFormat.JPEG => "JPEG",
        RasterFormat.TIFF => "TIFF",
        RasterFormat.Raw => "Raw",
        RasterFormat.COG => "COG",
        _ => "Unknown",
    };
}

internal sealed class CogTileDecodeContext
{
    public required long RegistrationId { get; init; }

    public required TileIndex Tile { get; init; }

    public required RasterFormat Format { get; init; }

    public required CogMetadata Metadata { get; init; }

    public required CogOverviewLevel Overview { get; init; }

    public CogOverzoomQuadrant? Quadrant { get; init; }

    public required string MetadataSource { get; init; }
}
