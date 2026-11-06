// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Honua.Infrastructure.Tiles;

internal static class TilePackageWriter
{
    private const int WebMercatorWkid = 3857;

    // This half-extent comes from the canonical constant so a truncated local copy cannot drift.
    private const double WebMercatorOrigin = Honua.Core.Features.Shared.Models.SpatialConstants.WebMercatorExtent;

    private const double ZeroLevelResolution = (WebMercatorOrigin * 2d) / 256d;

    private const int TileSize = 256;

    public const string TilePackageContentType = "application/octet-stream";

    public const string TilePackageStorageFormat = "tpk";

    internal readonly record struct PackagedTile(int Level, int Column, int Row, byte[] Bytes);

    public static async Task<int> WriteAsync(
        Stream destination,
        string cacheName,
        string tileExtension,
        string tileFormat,
        double[] bounds,
        IAsyncEnumerable<PackagedTile> tiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(tiles);

        var safeName = SanitizeCacheName(cacheName);
        var levels = new SortedSet<int>();
        var written = 0;

        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            await foreach (var tile in tiles.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                levels.Add(tile.Level);
                var entryPath = BuildTileEntryPath(safeName, tile.Level, tile.Row, tile.Column, tileExtension);
                var entry = archive.CreateEntry(entryPath, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(tile.Bytes, cancellationToken).ConfigureAwait(false);
                written++;
            }

            // conf.xml / conf.cdi describe the cache so ArcGIS clients can mount it.
            var minLevel = levels.Count > 0 ? levels.Min : 0;
            var maxLevel = levels.Count > 0 ? levels.Max : 0;
            WriteTextEntry(
                archive,
                $"v101/{safeName}/conf.xml",
                BuildCacheConfig(tileFormat, minLevel, maxLevel));
            WriteTextEntry(
                archive,
                $"v101/{safeName}/conf.cdi",
                BuildCacheDataInterval(bounds));
        }

        return written;
    }

    internal static string BuildTileEntryPath(string cacheName, int level, int row, int column, string extension)
    {
        var levelSegment = "L" + level.ToString("D2", CultureInfo.InvariantCulture);
        var rowSegment = "R" + row.ToString("x8", CultureInfo.InvariantCulture);
        var columnSegment = "C" + column.ToString("x8", CultureInfo.InvariantCulture);
        return $"v101/{cacheName}/_alllayers/{levelSegment}/{rowSegment}/{columnSegment}.{extension}";
    }

    private static void WriteTextEntry(ZipArchive archive, string entryPath, string content)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string BuildCacheConfig(string tileFormat, int minLevel, int maxLevel)
    {
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var writer = XmlWriter.Create(builder, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("CacheInfo");
        writer.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "typens:CacheInfo");
        writer.WriteAttributeString("xmlns", "typens", null, "http://www.esri.com/schemas/ArcGIS/10.1");

        writer.WriteStartElement("TileCacheInfo");
        writer.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "typens:TileCacheInfo");
        WriteSpatialReference(writer);
        writer.WriteElementString("TileOrigin", null, FormatPoint(-WebMercatorOrigin, WebMercatorOrigin));
        writer.WriteElementString("TileCols", TileSize.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString("TileRows", TileSize.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("LODInfos");

        for (var level = minLevel; level <= maxLevel; level++)
        {
            var resolution = ZeroLevelResolution / Math.Pow(2d, level);
            var scale = resolution / 0.00028d; // 0.28mm per pixel (Esri default DPI).
            writer.WriteStartElement("LODInfo");
            writer.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "typens:LODInfo");
            writer.WriteElementString("LevelID", level.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Scale", scale.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteElementString("Resolution", resolution.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // LODInfos
        writer.WriteEndElement(); // TileCacheInfo

        writer.WriteStartElement("TileImageInfo");
        writer.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "typens:TileImageInfo");
        writer.WriteElementString("CacheTileFormat", tileFormat);
        writer.WriteElementString("CompressionQuality", "0");
        writer.WriteElementString("Antialiasing", "true");
        writer.WriteEndElement(); // TileImageInfo

        writer.WriteElementString("CacheStorageInfo", null);
        writer.WriteEndElement(); // CacheInfo
        writer.WriteEndDocument();
        writer.Flush();
        return builder.ToString();
    }

    private static void WriteSpatialReference(XmlWriter writer)
    {
        writer.WriteStartElement("SpatialReference");
        writer.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "typens:ProjectedCoordinateSystem");
        writer.WriteElementString("WKID", WebMercatorWkid.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString("LatestWKID", WebMercatorWkid.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static string BuildCacheDataInterval(double[] bounds)
    {
        var west = bounds is { Length: 4 } ? bounds[0] : -180d;
        var south = bounds is { Length: 4 } ? bounds[1] : -85.05112878d;
        var east = bounds is { Length: 4 } ? bounds[2] : 180d;
        var north = bounds is { Length: 4 } ? bounds[3] : 85.05112878d;

        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var writer = XmlWriter.Create(builder, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("EnvelopeN");
        writer.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "typens:EnvelopeN");
        writer.WriteAttributeString("xmlns", "typens", null, "http://www.esri.com/schemas/ArcGIS/10.1");
        writer.WriteElementString("XMin", west.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteElementString("YMin", south.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteElementString("XMax", east.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteElementString("YMax", north.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return builder.ToString();
    }

    private static string FormatPoint(double x, double y)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{x:R} {y:R}");

    internal static string SanitizeCacheName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Layers";
        }

        var trimmed = value.Trim().Trim('/');
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return builder.Length == 0 ? "Layers" : builder.ToString();
    }
}
