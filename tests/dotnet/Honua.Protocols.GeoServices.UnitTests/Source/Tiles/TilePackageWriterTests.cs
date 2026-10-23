// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Infrastructure.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Tiles;

/// <summary>
/// Unit tests for the shared Esri exploded-cache tile-package (TPK) writer.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class TilePackageWriterTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_ProducesExplodedCacheLayoutWithConfig()
    {
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(0, 0, 0, [0x89, 0x50, 0x4E, 0x47]),
            new TilePackageWriter.PackagedTile(1, 1, 0, [0x89, 0x50, 0x4E, 0x47]),
        };

        using var stream = new MemoryStream();
        var written = await TilePackageWriter.WriteAsync(
            stream,
            "Sample Cache",
            "png",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None);

        written.Should().Be(2);

        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        // Sanitized cache name and zero-padded/hex coordinate segments.
        archive.GetEntry("v101/Sample_Cache/_alllayers/L00/R00000000/C00000000.png").Should().NotBeNull();
        archive.GetEntry("v101/Sample_Cache/_alllayers/L01/R00000000/C00000001.png").Should().NotBeNull();

        var confEntry = archive.GetEntry("v101/Sample_Cache/conf.xml");
        confEntry.Should().NotBeNull();
        archive.GetEntry("v101/Sample_Cache/conf.cdi").Should().NotBeNull();

        await using var confStream = confEntry!.Open();
        using var confReader = new StreamReader(confStream);
        var confXml = await confReader.ReadToEndAsync();
        var doc = XDocument.Parse(confXml);
        doc.Root.Should().NotBeNull();
        doc.Root!.Name.LocalName.Should().Be("CacheInfo");
        doc.Descendants().Where(e => e.Name.LocalName == "LODInfo").Should().HaveCount(2);
        doc.Descendants().First(e => e.Name.LocalName == "CacheTileFormat").Value.Should().Be("PNG");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void BuildTileEntryPath_UsesEsriZeroPaddedHexSegments()
    {
        var path = TilePackageWriter.BuildTileEntryPath("Layers", level: 12, row: 2730, column: 1364, extension: "png");
        path.Should().Be("v101/Layers/_alllayers/L12/R00000aaa/C00000554.png");
    }

    private static async IAsyncEnumerable<TilePackageWriter.PackagedTile> ToAsync(
        IEnumerable<TilePackageWriter.PackagedTile> tiles)
    {
        foreach (var tile in tiles)
        {
            yield return tile;
        }

        await Task.CompletedTask;
    }
}
