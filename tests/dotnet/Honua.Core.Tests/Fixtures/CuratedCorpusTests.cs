// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Fixtures;

public sealed class CuratedCorpusTests
{
    [UnitTest]
    public void Manifest_VerifiesEveryCommittedAsset()
    {
        var corpus = CuratedCorpus.Load();

        corpus.VerifyAll();

        Assert.Equal("v1", corpus.Revision);
        Assert.Equal(9, corpus.Assets.Count);
        Assert.All(corpus.Assets, asset =>
        {
            Assert.Equal(64, asset.Sha256.Length);
            Assert.NotEmpty(asset.MediaType);
            Assert.NotEmpty(asset.Facets);
        });
    }

    [UnitTest]
    public void DegenerateGeometryAsset_PreservesCollapsedAndEmptyShapes()
    {
        var corpus = CuratedCorpus.Load();
        using var document = JsonDocument.Parse(corpus.ReadAllBytes("degenerate-geometries"));
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();

        Assert.Equal(5, features.Length);
        Assert.Equal(3, features[0].GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
        Assert.Equal(4, features[1].GetProperty("geometry").GetProperty("coordinates")[0].GetArrayLength());
        Assert.Empty(features[3].GetProperty("geometry").GetProperty("geometries").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, features[4].GetProperty("geometry").ValueKind);
    }

    [UnitTest]
    public void GeometryAsset_PreservesMultipartHolesAndUnicodeAttributes()
    {
        var corpus = CuratedCorpus.Load();
        using var document = JsonDocument.Parse(corpus.ReadAllBytes("edge-geometries"));
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();

        Assert.Contains(features, feature => feature.GetProperty("geometry").GetProperty("type").GetString() == "MultiPoint");
        Assert.Contains(features, feature => feature.GetProperty("geometry").GetProperty("type").GetString() == "MultiLineString");
        var polygon = Assert.Single(features, feature => feature.GetProperty("id").GetString() == "multipolygon-hole");
        var polygons = polygon.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().ToArray();
        Assert.Equal(2, polygons.Length);
        Assert.Equal(2, polygons[0].GetArrayLength());

        var text = Encoding.UTF8.GetString(corpus.ReadAllBytes("edge-geometries"));
        Assert.Contains("Hāna – 東京", text, StringComparison.Ordinal);
        Assert.Contains("emoji 🌺", text, StringComparison.Ordinal);
    }

    [UnitTest]
    public void CrsAndWarehouseAssets_PreserveEdgeSchemaValues()
    {
        var corpus = CuratedCorpus.Load();
        var crs = Encoding.UTF8.GetString(corpus.ReadAllBytes("crs-cases"));
        var facts = Encoding.UTF8.GetString(corpus.ReadAllBytes("warehouse-fact-table"));

        Assert.Contains("EPSG:4326,yx", crs, StringComparison.Ordinal);
        Assert.Contains("EPSG:3857", crs, StringComparison.Ordinal);
        Assert.Contains("EPSG:26904", crs, StringComparison.Ordinal);
        Assert.Contains("OGC:CRS84", crs, StringComparison.Ordinal);
        Assert.Contains("9007199254740992", facts, StringComparison.Ordinal);
        Assert.Contains("2026-01-02T12:34:56.123456Z", facts, StringComparison.Ordinal);
        Assert.Contains("tenant-東京", facts, StringComparison.Ordinal);
        Assert.Contains("\"quoted, value\"", facts, StringComparison.Ordinal);
    }

    [UnitTest]
    public void ZarrAsset_IsARealTwoSliceFloat32Cube()
    {
        var corpus = CuratedCorpus.Load();
        using var metadata = JsonDocument.Parse(corpus.ReadAllBytes("sst-temperature-array"));
        var shape = metadata.RootElement.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var chunk = corpus.ReadAllBytes("sst-temperature-chunk-0");

        Assert.Equal([2, 3, 4], shape);
        Assert.Equal("<f4", metadata.RootElement.GetProperty("dtype").GetString());
        Assert.Equal(24 * sizeof(float), chunk.Length);
        Assert.Equal(10f, ReadSingle(chunk, 0));
        Assert.Equal(21f, ReadSingle(chunk, 11));
        Assert.Equal(35f, ReadSingle(chunk, 12));
        Assert.Equal(24f, ReadSingle(chunk, 23));
    }

    [UnitTest]
    public void Resolver_FailsClosedOnDigestMismatch()
    {
        using var temporary = TemporaryCorpus.Create(copyEdgeAsset: true);
        var assetPath = Path.Join(temporary.Path, "edge-geometries.geojson");
        var bytes = File.ReadAllBytes(assetPath);
        bytes[0] ^= 0xff;
        File.WriteAllBytes(assetPath, bytes);
        var corpus = CuratedCorpus.LoadFromDirectory(temporary.Path);

        var error = Assert.Throws<InvalidDataException>(() => corpus.ResolveVerifiedPath("edge-geometries"));

        Assert.Contains("digest mismatch", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void Resolver_FailsClosedOnPathEscape()
    {
        using var temporary = TemporaryCorpus.Create(copyEdgeAsset: false);
        var manifestPath = Path.Join(temporary.Path, "manifest.json");
        var manifest = File.ReadAllText(manifestPath).Replace(
            "\"edge-geometries.geojson\"",
            "\"../edge-geometries.geojson\"",
            StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest);
        var corpus = CuratedCorpus.LoadFromDirectory(temporary.Path);

        var error = Assert.Throws<InvalidDataException>(() => corpus.ResolveVerifiedPath("edge-geometries"));

        Assert.Contains("unsafe segment", error.Message, StringComparison.Ordinal);
    }

    private static float ReadSingle(byte[] bytes, int index)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float))));

    private sealed class TemporaryCorpus : IDisposable
    {
        private TemporaryCorpus(string path) => Path = path;

        public string Path { get; }

        public static TemporaryCorpus Create(bool copyEdgeAsset)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"honua-curated-corpus-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            var source = RepositoryPaths.Resolve("tests", "fixtures", "curated-edge-corpus", "v1");
            File.Copy(System.IO.Path.Join(source, "manifest.json"), System.IO.Path.Join(path, "manifest.json"));
            if (copyEdgeAsset)
            {
                File.Copy(System.IO.Path.Join(source, "edge-geometries.geojson"), System.IO.Path.Join(path, "edge-geometries.geojson"));
            }

            return new TemporaryCorpus(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
