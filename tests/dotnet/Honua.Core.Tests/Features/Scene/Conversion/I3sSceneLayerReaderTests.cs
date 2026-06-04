// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using Honua.Core.Features.Scene.Conversion;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Conversion;

/// <summary>
/// Unit tests for the I3S scene-layer descriptor reader, including reading the
/// gzip-compressed descriptor out of an in-memory <c>.slpk</c> zip (#1268).
/// </summary>
public sealed class I3sSceneLayerReaderTests
{
    private const string SampleSceneLayerJson = """
    {
      "id": 0,
      "layerType": "3DObject",
      "name": "Sample Buildings",
      "version": "1.7",
      "spatialReference": { "wkid": 4326, "latestWkid": 4326 },
      "fullExtent": { "xmin": -122.5, "ymin": 37.7, "xmax": -122.4, "ymax": 37.8, "zmin": 0, "zmax": 120 },
      "store": { "id": "store-1", "profile": "meshpyramids" }
    }
    """;

    [UnitTest]
    public void Parse_ValidDescriptor_BindsAllMappedFields()
    {
        var layer = I3sSceneLayerReader.Parse(Encoding.UTF8.GetBytes(SampleSceneLayerJson));

        layer.Id.Should().Be(0);
        layer.LayerType.Should().Be("3DObject");
        layer.Name.Should().Be("Sample Buildings");
        layer.SpatialReference!.Wkid.Should().Be(4326);
        layer.FullExtent!.Xmin.Should().Be(-122.5);
        layer.FullExtent.Zmax.Should().Be(120.0);
        layer.Store!.Profile.Should().Be("meshpyramids");
    }

    [UnitTest]
    public void Parse_MalformedJson_ThrowsWithStableReason()
    {
        var act = () => I3sSceneLayerReader.Parse(Encoding.UTF8.GetBytes("{ not json"));

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.MalformedSceneLayer);
    }

    [UnitTest]
    public void ReadFromSlpk_GzipDescriptor_ParsesLayer()
    {
        using var slpk = BuildSlpk(I3sSceneLayerReader.SceneLayerGzipEntryName, gzip: true);

        var layer = I3sSceneLayerReader.ReadFromSlpk(slpk);

        layer.LayerType.Should().Be("3DObject");
        layer.FullExtent!.Xmax.Should().Be(-122.4);
    }

    [UnitTest]
    public void ReadFromSlpk_PlainDescriptor_ParsesLayer()
    {
        using var slpk = BuildSlpk(I3sSceneLayerReader.SceneLayerEntryName, gzip: false);

        var layer = I3sSceneLayerReader.ReadFromSlpk(slpk);

        layer.Name.Should().Be("Sample Buildings");
    }

    [UnitTest]
    public void ReadFromSlpk_MissingDescriptor_ThrowsMissingSceneLayer()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("metadata.json");
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes("{}"));
        }

        buffer.Position = 0;
        var act = () => I3sSceneLayerReader.ReadFromSlpk(buffer);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.MissingSceneLayer);
    }

    [UnitTest]
    public void ReadFromSlpk_InvalidArchive_ThrowsInvalidArchive()
    {
        using var notAZip = new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip archive at all"));

        var act = () => I3sSceneLayerReader.ReadFromSlpk(notAZip);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.InvalidArchive);
    }

    [UnitTest]
    public void ReadFromSlpk_OversizedGzipDescriptor_ThrowsInvalidArchive()
    {
        // A small, highly-compressible gzip entry that inflates past the
        // decompression cap (a gzip-bomb) must be rejected rather than buffered.
        var oversized = I3sSceneLayerReader.MaxDescriptorBytes + (1024 * 1024);
        using var slpk = new MemoryStream();
        using (var archive = new ZipArchive(slpk, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(I3sSceneLayerReader.SceneLayerGzipEntryName);
            using var entryStream = entry.Open();
            using var gzipStream = new GZipStream(entryStream, CompressionLevel.Optimal, leaveOpen: true);
            var chunk = new byte[64 * 1024];
            long written = 0;
            while (written < oversized)
            {
                gzipStream.Write(chunk, 0, chunk.Length);
                written += chunk.Length;
            }
        }

        slpk.Position = 0;
        var act = () => I3sSceneLayerReader.ReadFromSlpk(slpk);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.InvalidArchive);
    }

    private static MemoryStream BuildSlpk(string entryName, bool gzip)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var entryStream = entry.Open();
            var payload = Encoding.UTF8.GetBytes(SampleSceneLayerJson);
            if (gzip)
            {
                using var gzipStream = new GZipStream(entryStream, CompressionLevel.Optimal, leaveOpen: true);
                gzipStream.Write(payload);
            }
            else
            {
                entryStream.Write(payload);
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
