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
        Assert.Equal(14, corpus.Assets.Count);
        Assert.All(corpus.Assets, asset =>
        {
            Assert.Equal(64, asset.Sha256.Length);
            Assert.NotEmpty(asset.MediaType);
            Assert.NotEmpty(asset.Facets);
        });
    }

    [UnitTest]
    public void MultibyteRtlAsset_PreservesScriptsCombiningMarksAndBidiControls()
    {
        var corpus = CuratedCorpus.Load();
        var text = Encoding.UTF8.GetString(corpus.ReadAllBytes("multibyte-rtl-attributes"));
        using var document = JsonDocument.Parse(text);
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();

        Assert.Equal("مرحبا بالعالم", features[0].GetProperty("properties").GetProperty("label").GetString());
        Assert.Equal("שלום עולם", features[1].GetProperty("properties").GetProperty("label").GetString());
        Assert.Equal("نقشهٔ هونوا", features[2].GetProperty("properties").GetProperty("label").GetString());
        Assert.Contains("東京 🌺", text, StringComparison.Ordinal);
        Assert.Contains('\u0301', features[3].GetProperty("properties").GetProperty("decomposed").GetString()!);
        Assert.Contains('\u2067', features[3].GetProperty("properties").GetProperty("isolated").GetString()!);
        Assert.Contains('\u2069', features[3].GetProperty("properties").GetProperty("isolated").GetString()!);
    }

    [UnitTest]
    public void MalformedAsset_RemainsJsonParseableWhileRetainingSemanticFaults()
    {
        var corpus = CuratedCorpus.Load();
        var bytes = corpus.ReadAllBytes("malformed-parseable");
        using var document = JsonDocument.Parse(bytes);
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();

        Assert.Equal(5, features.Length);
        Assert.Equal(JsonValueKind.Number, features[1].GetProperty("geometry").GetProperty("coordinates").ValueKind);
        Assert.Equal("Rhombus", features[2].GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, features[3].GetProperty("properties").GetProperty("height").ValueKind);
        Assert.False(features[4].TryGetProperty("properties", out _));
        Assert.Contains("\"id\":\"duplicate-member\",\"id\":", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [UnitTest]
    public void LargeAttributeAsset_PreservesPayloadWithoutTruncation()
    {
        var corpus = CuratedCorpus.Load();
        using var document = JsonDocument.Parse(corpus.ReadAllBytes("large-attributes"));
        var properties = document.RootElement.GetProperty("properties");
        var payload = properties.GetProperty("payload").GetString();

        Assert.NotNull(payload);
        Assert.Equal(16_384, payload.Length);
        Assert.StartsWith("0123456789abcdef", payload, StringComparison.Ordinal);
        Assert.EndsWith("0123456789abcdef", payload, StringComparison.Ordinal);
        Assert.Equal("END-OF-LARGE-ATTRIBUTE", properties.GetProperty("tailMarker").GetString());
    }

    [UnitTest]
    public void ExtremeCoordinateAsset_PreservesFiniteRangeEdges()
    {
        var corpus = CuratedCorpus.Load();
        using var document = JsonDocument.Parse(corpus.ReadAllBytes("extreme-coordinate-ranges"));
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();

        Assert.Equal(179.99999999999997, features[0].GetProperty("geometry").GetProperty("coordinates")[0][0].GetDouble());
        Assert.Equal(1_000_000, features[1].GetProperty("geometry").GetProperty("coordinates")[0].GetDouble());
        Assert.Equal(1e150, features[2].GetProperty("geometry").GetProperty("coordinates")[0].GetDouble());
        Assert.True(double.IsFinite(features[3].GetProperty("geometry").GetProperty("coordinates")[0].GetDouble()));
    }

    [UnitTest]
    public void MixedCrsAsset_KeepsPerFeatureSourceReferenceSystems()
    {
        var corpus = CuratedCorpus.Load();
        using var document = JsonDocument.Parse(corpus.ReadAllBytes("mixed-crs-features"));
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        var sourceSystems = features.Select(feature => feature.GetProperty("properties").GetProperty("sourceCrs").GetString()!).ToArray();

        Assert.Equal(4, features.Length);
        Assert.Equal(["EPSG:4326", "EPSG:3857", "EPSG:26904", "OGC:CRS84"], sourceSystems);
        Assert.Equal("yx", features[0].GetProperty("properties").GetProperty("axisOrder").GetString());
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
