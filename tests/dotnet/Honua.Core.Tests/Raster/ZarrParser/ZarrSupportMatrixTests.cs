// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrSupportMatrixTests
{
    private const string Root = "stores/matrix";

    public static IEnumerable<object[]> SupportedCells()
        => ZarrSupportMatrix.Cells.Select(cell => new object[] { cell });

    [Theory]
    [MemberData(nameof(SupportedCells))]
    [Trait("Tier", "Fast")]
    public async Task ReadSubsetAsync_AdvertisedCell_PreservesSamples(ZarrSupportCell cell)
    {
        var objects = BuildFixture(cell);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", Root);
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", Root, metadata,
            new ZarrSubsetRequest { Variable = "matrix", Start = [0, 0], Stop = [2, 2] });
        subset.Data.Should().Equal(BuildSamples(cell));
        ZarrValueDecoder.TryDecode(subset.Data, subset.DataType, out var value).Should().BeTrue();
        value.Should().Be(1);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void WriteJson_CommittedProjection_MatchesRuntimeContract()
    {
        using var output = new MemoryStream();
        ZarrSupportMatrix.WriteJson(output);
        var root = FindRepositoryRoot();
        var path = Path.Join(root, "docs/gis/data/zarr-support-matrix.v1.json");
        if (Environment.GetEnvironmentVariable("HONUA_EMIT_ZARR_SUPPORT_MATRIX") == "1")
            File.WriteAllBytes(path, output.ToArray());
        File.ReadAllBytes(path).Should().Equal(output.ToArray());
        var documentation = File.ReadAllText(Path.Join(root, "docs/reference/protocols/zarr-support.md"));
        documentation.Should().Contain("../../gis/data/zarr-support-matrix.v1.json")
            .And.Contain($"`{ZarrSupportMatrix.Format}`");
        var summary = new StringBuilder("<!-- zarr-support-summary:start -->\n\n");
        summary.AppendLine("| Version | Fixture cells | Scalar types | Chunk codecs |");
        summary.AppendLine("| --- | ---: | --- | --- |");
        foreach (var version in ZarrSupportMatrix.Cells.GroupBy(cell => cell.Version).OrderBy(group => group.Key))
        {
            var types = string.Join(", ", version.Select(cell => cell.NormalizedDataType).Distinct());
            var codecs = string.Join(", ", version.Select(cell => cell.Codec).Distinct());
            summary.AppendLine($"| {version.Key} | {version.Count()} | {types} | {codecs} |");
        }
        summary.Append("\n<!-- zarr-support-summary:end -->");
        documentation.Should().Contain(summary.ToString());
    }

    public sealed record RejectionCase
    {
        public required int Version { get; init; }
        public required string Field { get; init; }
        public required string Value { get; init; }
        public required string Reason { get; init; }
    }

    public static IEnumerable<object[]> RejectedFamilies()
    {
        yield return [new RejectionCase { Version = 2, Field = "dtype", Value = "<f16", Reason = ZarrSupportMatrix.UnsupportedDataType }];
        yield return [new RejectionCase { Version = 2, Field = "dtype", Value = "<i3", Reason = ZarrSupportMatrix.UnsupportedDataType }];
        yield return [new RejectionCase { Version = 2, Field = "dtype", Value = "[[\"x\",\"<i4\"]]", Reason = ZarrSupportMatrix.UnsupportedDataType }];
        yield return [new RejectionCase { Version = 2, Field = "dtype", Value = ">i2", Reason = ZarrSupportMatrix.UnsupportedEndian }];
        yield return [new RejectionCase { Version = 2, Field = "order", Value = "F", Reason = ZarrSupportMatrix.UnsupportedOrder }];
        yield return [new RejectionCase { Version = 2, Field = "dimension_separator", Value = "-", Reason = ZarrSupportMatrix.UnsupportedChunkKey }];
        yield return [new RejectionCase { Version = 2, Field = "compressor", Value = "{\"id\":\"blosc\"}", Reason = ZarrSupportMatrix.UnsupportedCodec }];
        yield return [new RejectionCase { Version = 2, Field = "filters", Value = "[{\"id\":\"delta\"}]", Reason = ZarrSupportMatrix.UnsupportedFilter }];
        yield return [new RejectionCase { Version = 3, Field = "data_type", Value = "complex64", Reason = ZarrSupportMatrix.UnsupportedDataType }];
        yield return [new RejectionCase { Version = 3, Field = "data_type", Value = "{\"name\":\"custom\"}", Reason = ZarrSupportMatrix.UnsupportedDataType }];
        yield return [new RejectionCase { Version = 3, Field = "chunk_key_encoding", Value = "unknown", Reason = ZarrSupportMatrix.UnsupportedChunkKey }];
        yield return [new RejectionCase { Version = 3, Field = "codecs", Value = "[{\"name\":\"gzip\"}]", Reason = ZarrSupportMatrix.UnsupportedPipeline }];
        yield return [new RejectionCase { Version = 3, Field = "codecs", Value = "[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"big\"}}]", Reason = ZarrSupportMatrix.UnsupportedEndian }];
        yield return [new RejectionCase { Version = 3, Field = "codecs", Value = "[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}},{\"name\":\"zstd\"}]", Reason = ZarrSupportMatrix.UnsupportedCodec }];
    }

    [Theory]
    [MemberData(nameof(RejectedFamilies))]
    [Trait("Tier", "Fast")]
    public async Task ReadMetadataAsync_RejectedFamily_ReturnsStableReason(RejectionCase rejection)
    {
        var cell = ZarrSupportMatrix.Cells.First(cell => cell.Version == rejection.Version && cell.NormalizedDataType == "<f4");
        var objects = BuildFixture(cell);
        var key = Root + (rejection.Version == 2 ? "/.zarray" : "/zarr.json");
        var node = JsonNode.Parse(objects[key])!;
        node[rejection.Field] = rejection.Value.StartsWith('{') || rejection.Value.StartsWith('[')
            ? JsonNode.Parse(rejection.Value) : JsonValue.Create(rejection.Value);
        objects[key] = Encoding.UTF8.GetBytes(node.ToJsonString());
        var read = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        var error = (await read.Should().ThrowAsync<InvalidDataException>()).Which;
        error.Data[ZarrSupportMatrix.ReasonCodeKey].Should().Be(rejection.Reason);
        ZarrSupportMatrix.RejectionCodes.Should().Contain(rejection.Reason);
    }

    private static Dictionary<string, byte[]> BuildFixture(ZarrSupportCell cell)
    {
        var v3 = cell.Version == 3;
        var objects = v3
            ? ZarrFixtureBuilder.BuildV3SingleArray(Root, 2, 2, 2, 2, (r, c) => 1, gzip: false)
            : ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 2, 2, 2, 2, (r, c) => 1);
        var key = Root + (v3 ? "/zarr.json" : "/.zarray");
        var node = JsonNode.Parse(objects[key])!.AsObject();
        if (v3)
        {
            node["data_type"] = cell.DataType;
            node["chunk_key_encoding"] = new JsonObject
            {
                ["name"] = cell.ChunkKeyEncoding,
                ["configuration"] = new JsonObject { ["separator"] = cell.Separator }
            };
            var codecs = new JsonArray(new JsonObject
            {
                ["name"] = "bytes",
                ["configuration"] = cell.Endian is null ? new JsonObject() : new JsonObject { ["endian"] = cell.Endian }
            });
            if (cell.Codec != "none")
                codecs.Add(new JsonObject { ["name"] = cell.Codec, ["configuration"] = new JsonObject { ["level"] = 5 } });
            node["codecs"] = codecs;
        }
        else
        {
            node["dtype"] = cell.DataType;
            node["dimension_separator"] = cell.Separator;
            node["compressor"] = cell.Codec == "none" ? null : new JsonObject { ["id"] = cell.Codec, ["level"] = 5 };
        }
        node["fill_value"] = cell.IsBoolean ? JsonValue.Create(false) : JsonValue.Create(0);
        objects[key] = Encoding.UTF8.GetBytes(node.ToJsonString());
        objects.Remove(Root + (v3 ? "/c/0/0" : "/0.0"));
        var chunkKey = (cell.ChunkKeyEncoding == "default" ? "c" + cell.Separator : "") + "0" + cell.Separator + "0";
        objects[Root + "/" + chunkKey] = Encode(BuildSamples(cell), cell.Codec);
        return objects;
    }

    private static byte[] BuildSamples(ZarrSupportCell cell)
    {
        var sample = cell.NormalizedDataType[1..] switch
        {
            "f4" => BitConverter.GetBytes(1f),
            "f8" => BitConverter.GetBytes(1d),
            _ => new byte[cell.ElementBytes]
        };
        if (cell.NormalizedDataType[1] != 'f') sample[0] = 1;
        return Enumerable.Range(1, 4).SelectMany(value =>
        {
            if (cell.IsBoolean) return new byte[] { (byte)(value % 2) };
            if (cell.NormalizedDataType.EndsWith("f4", StringComparison.Ordinal)) return BitConverter.GetBytes((float)value);
            if (cell.NormalizedDataType.EndsWith("f8", StringComparison.Ordinal)) return BitConverter.GetBytes((double)value);
            var bytes = (byte[])sample.Clone();
            bytes[0] = (byte)value;
            return bytes;
        }).ToArray();
    }

    private static byte[] Encode(byte[] samples, string codec)
    {
        if (codec == "none") return samples;
        using var output = new MemoryStream();
        using (Stream encoder = codec == "gzip"
            ? new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)
            : new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            encoder.Write(samples);
        return output.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Join(directory.FullName, "Honua.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not find Honua.sln.");
    }
}
