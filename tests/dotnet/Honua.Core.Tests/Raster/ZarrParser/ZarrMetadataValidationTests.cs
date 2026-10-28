// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.IO.Compression;
using Honua.Core.Features.Raster.Domain;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrMetadataValidationTests
{
    private const string Root = "stores/validation";

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("zarr_format")]
    [InlineData("node_type")]
    [InlineData("shape")]
    [InlineData("data_type")]
    [InlineData("chunk_grid")]
    [InlineData("chunk_key_encoding")]
    [InlineData("fill_value")]
    [InlineData("codecs")]
    public async Task ReadMetadataAsync_V3RequiredArrayFieldMissing_RejectsRootAndChild(string field)
    {
        foreach (var grouped in new[] { false, true })
        {
            var objects = BuildV3(grouped);
            Mutate(objects, grouped ? Root + "/temperature/zarr.json" : Root + "/zarr.json", node => node.Remove(field));
            var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
            await act.Should().ThrowAsync<InvalidDataException>($"{field} is required on every array node");
        }
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("zarr_format", "\"3\"")]
    [InlineData("zarr_format", "2")]
    [InlineData("zarr_format", "null")]
    [InlineData("node_type", "\"other\"")]
    [InlineData("node_type", "null")]
    [InlineData("attributes", "[]")]
    [InlineData("attributes", "null")]
    [InlineData("data_type", "{\"name\":\"float32\"}")]
    [InlineData("dimension_names", "[\"x\"]")]
    [InlineData("dimension_names", "[1,\"x\"]")]
    [InlineData("dimension_names", "null")]
    [InlineData("storage_transformers", "[ {\"name\":\"unknown\"} ]")]
    [InlineData("chunk_key_encoding", "{\"name\":\"default\",\"configuration\":{\"separator\":\"invalid\"}}")]
    public async Task ReadMetadataAsync_V3InvalidOrUnsupportedMetadata_RejectsBeforePixels(string field, string value)
    {
        var objects = BuildV3(false);
        Mutate(objects, Root + "/zarr.json", node => node[field] = JsonNode.Parse(value));
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"must_understand\":true}")]
    [InlineData("{\"must_understand\":\"false\"}")]
    [InlineData("null")]
    public async Task ReadMetadataAsync_V3UnknownRequiredField_RejectsRootAndChild(string value)
    {
        foreach (var grouped in new[] { false, true })
        {
            var objects = BuildV3(grouped);
            Mutate(objects, grouped ? Root + "/temperature/zarr.json" : Root + "/zarr.json", node => node["unknown_extension"] = JsonNode.Parse(value));
            var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
            await act.Should().ThrowAsync<InvalidDataException>();
        }
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("zarr_format", "null")]
    [InlineData("zarr_format", "\"3\"")]
    [InlineData("node_type", "\"other\"")]
    [InlineData("attributes", "[]")]
    public async Task ReadMetadataAsync_V3GroupInvalidMetadata_Rejects(string field, string value)
    {
        var objects = BuildV3(true);
        Mutate(objects, Root + "/zarr.json", node => node[field] = JsonNode.Parse(value));
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadMetadataAsync_V3OptionalExtensionAndNullDimensionNames_PreservesSupportedArray(bool grouped)
    {
        var objects = BuildV3(grouped);
        Mutate(objects, grouped ? Root + "/temperature/zarr.json" : Root + "/zarr.json", node =>
        {
            node["unknown_extension"] = JsonNode.Parse("{\"name\":\"unknown\",\"must_understand\":false}");
            node["dimension_names"] = JsonNode.Parse("[null,\"x\"]");
            node["storage_transformers"] = new JsonArray();
        });
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        metadata.Arrays.Should().ContainSingle();
        metadata.Arrays[0].Shape.Should().Equal(2, 2);
        metadata.Arrays[0].DimensionNames![1].Should().Be("x");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("{\"kind\":\"inline\",\"metadata\":{}}")]
    [InlineData("{\"must_understand\":true,\"kind\":\"inline\",\"metadata\":{}}")]
    [InlineData("{\"must_understand\":false,\"kind\":\"other\",\"metadata\":{}}")]
    [InlineData("{\"must_understand\":false,\"kind\":\"inline\",\"metadata\":[]}")]
    public async Task ReadMetadataAsync_V3MalformedConsolidation_Rejects(string value)
    {
        var objects = BuildV3(true);
        Mutate(objects, Root + "/zarr.json", node => node["consolidated_metadata"] = JsonNode.Parse(value));
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V3ConsolidationWithoutManifest_ExplainsUnsupportedDiscovery()
    {
        var objects = BuildV3(true);
        Mutate(objects, Root + "/zarr.json", node =>
        {
            node["attributes"]!.AsObject().Remove("variables");
            node["consolidated_metadata"] = JsonNode.Parse("{\"must_understand\":false,\"kind\":\"inline\",\"metadata\":{}}");
        });
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*manifest*not supported*");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("zarr_format")]
    [InlineData("shape")]
    [InlineData("chunks")]
    [InlineData("dtype")]
    [InlineData("compressor")]
    [InlineData("fill_value")]
    [InlineData("order")]
    [InlineData("filters")]
    public async Task ReadMetadataAsync_V2RequiredArrayFieldMissing_Rejects(string field)
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 2, 2, 2, 2, (r, c) => r + c);
        Mutate(objects, Root + "/.zarray", node => node.Remove(field));
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"zarr_format\":3}")]
    [InlineData("{\"zarr_format\":\"2\"}")]
    [InlineData("{\"zarr_format\":2,\"extra\":{}}")]
    public async Task ReadMetadataAsync_V2InvalidGroupDocument_RejectsBeforeManifestDiscovery(string group)
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(Root, 2, 2, 2, 2, (r, c) => r + c, 4326, 0, 0, 2, 2);
        objects[Root + "/.zgroup"] = Encoding.UTF8.GetBytes(group);
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V2OptionalSeparatorOmitted_PreservesDotChunkKeys()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 2, 2, 2, 2, (r, c) => r + c);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        metadata.Arrays[0].ChunkKeyEncoding.Separator.Should().Be(".");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V2UnknownArrayField_IgnoresWithoutApplyingV3ExtensionRules()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 2, 2, 2, 2, (r, c) => r + c);
        Mutate(objects, Root + "/.zarray", node => node["unknown_field"] = 123);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        metadata.Arrays.Should().ContainSingle();
        metadata.Arrays[0].Shape.Should().Equal(2, 2);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("2")]
    [InlineData("\"3\"")]
    [InlineData("null")]
    public async Task ReadMetadataAsync_V3ChildArrayWrongVersion_RejectsDespiteValidParent(string version)
    {
        var objects = BuildV3(true);
        Mutate(objects, Root + "/temperature/zarr.json", node => node["zarr_format"] = JsonNode.Parse(version));
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadMetadataAsync_V3IgnorableExtensionWithNonObjectConfiguration_Rejects(bool storageTransformer)
    {
        var objects = BuildV3(false);
        Mutate(objects, Root + "/zarr.json", node =>
        {
            var extension = JsonNode.Parse("{\"name\":\"unknown\",\"must_understand\":false,\"configuration\":7}");
            node[storageTransformer ? "storage_transformers" : "unknown_extension"] = storageTransformer
                ? new JsonArray(extension)
                : extension;
        });
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("chunk_grid", "false")]
    [InlineData("chunk_key_encoding", "false")]
    [InlineData("chunk_grid", "7")]
    [InlineData("chunk_key_encoding", "null")]
    [InlineData("codecs", "\"true\"")]
    public async Task ReadMetadataAsync_V3InvalidKnownExtensionFlag_Rejects(string field, string flag)
    {
        var objects = BuildV3(false);
        Mutate(objects, Root + "/zarr.json", node =>
        {
            var extension = field == "codecs" ? node[field]![0]! : node[field]!;
            extension["must_understand"] = JsonNode.Parse(flag);
        });
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadMetadataAsync_V3ValidKnownExtensionFlags_PreservesArray(bool codecRequired)
    {
        var objects = BuildV3(false);
        Mutate(objects, Root + "/zarr.json", node =>
        {
            node["chunk_grid"]!["must_understand"] = true;
            node["chunk_key_encoding"]!["must_understand"] = true;
            node["codecs"]![0]!["must_understand"] = codecRequired;
        });
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        metadata.Arrays[0].Shape.Should().Equal(2, 2);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadMetadataAsync_ValidScalarOrEmptyArray_ExplainsUnsupportedCoverageShape(bool v3, bool scalar)
    {
        var objects = v3 ? BuildV3(false) : ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 2, 2, 2, 2, (r, c) => 0);
        Mutate(objects, Root + (v3 ? "/zarr.json" : "/.zarray"), node =>
        {
            node["shape"] = JsonNode.Parse(scalar ? "[]" : "[0,2]");
            if (scalar)
            {
                if (v3)
                {
                    node["chunk_grid"]!["configuration"]!["chunk_shape"] = new JsonArray();
                    node["dimension_names"] = new JsonArray();
                }
                else node["chunks"] = new JsonArray();
            }
        });
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*not supported*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V2InvalidOrder_IsNotMisreportedAsFortranSupport()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 2, 2, 2, 2, (r, c) => 0);
        Mutate(objects, Root + "/.zarray", node => node["order"] = "invalid");
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*invalid*order*");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("default", false)]
    [InlineData("v2", false)]
    [InlineData("default", true)]
    [InlineData("v2", true)]
    public async Task ReadSubsetAsync_V31ShorthandBytesAndChunkKeys_PreservesSamples(string keyEncoding, bool gzip)
    {
        var objects = BuildV3(false);
        Mutate(objects, Root + "/zarr.json", node =>
        {
            node["data_type"] = "uint8";
            node["codecs"] = JsonNode.Parse(gzip
                ? "[\"bytes\",{\"name\":\"gzip\",\"configuration\":{\"level\":5}}]"
                : "[\"bytes\"]");
            node["chunk_key_encoding"] = keyEncoding;
        });
        byte[] raw = [1, 2, 3, 4];
        if (gzip)
        {
            using var output = new MemoryStream();
            using (var encoder = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                encoder.Write(raw);
            raw = output.ToArray();
        }
        objects.Remove(Root + "/c/0/0");
        objects[Root + (keyEncoding == "default" ? "/c/0/0" : "/0.0")] = raw;
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", Root);
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", Root, metadata,
            new ZarrSubsetRequest { Variable = "validation", Start = [0, 0], Stop = [2, 2] });
        subset.Data.Should().Equal(1, 2, 3, 4);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V31ShorthandBytesForMultiByteSamples_StillRequiresEndianness()
    {
        var objects = BuildV3(false);
        Mutate(objects, Root + "/zarr.json", node => node["codecs"] = JsonNode.Parse("[\"bytes\"]"));
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*endianness*");
    }

    private static Dictionary<string, byte[]> BuildV3(bool grouped) => grouped
        ? ZarrFixtureBuilder.BuildV3Group(Root, 2, 2, 2, 2, (r, c) => r + c, 4326, 0, 0, 2, 2)
        : ZarrFixtureBuilder.BuildV3SingleArray(Root, 2, 2, 2, 2, (r, c) => r + c, gzip: false);

    private static void Mutate(Dictionary<string, byte[]> objects, string key, Action<JsonObject> mutation)
    {
        var node = JsonNode.Parse(objects[key])!.AsObject();
        mutation(node);
        objects[key] = Encoding.UTF8.GetBytes(node.ToJsonString());
    }
}
