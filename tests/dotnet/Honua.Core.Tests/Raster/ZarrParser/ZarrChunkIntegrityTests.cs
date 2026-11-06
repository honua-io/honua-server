// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrChunkIntegrityTests
{
    private const string Root = "datasets/integrity";

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"id\":7}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("7")]
    public async Task ReadMetadataAsync_MalformedV2Compressor_RejectsInsteadOfUsingRawSamples(string compressor)
    {
        var objects = BuildRaw();
        var document = JsonNode.Parse(objects[Root + "/.zarray"])!;
        document["compressor"] = JsonNode.Parse(compressor);
        objects[Root + "/.zarray"] = Encoding.UTF8.GetBytes(document.ToJsonString());
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadSubsetAsync_V2EmptyFilters_PreservesUncompressedSamples()
    {
        var objects = BuildRaw();
        var document = JsonNode.Parse(objects[Root + "/.zarray"])!;
        document["filters"] = new JsonArray();
        objects[Root + "/.zarray"] = Encoding.UTF8.GetBytes(document.ToJsonString());
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", Root);
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", Root, metadata,
            new ZarrSubsetRequest { Variable = "integrity", Start = [0, 0], Stop = [2, 2] });

        subset.Data.Should().Equal(objects[Root + "/0.0"]);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("none", "empty")]
    [InlineData("gzip", "empty")]
    [InlineData("zlib", "empty")]
    [InlineData("none", "extra-samples")]
    [InlineData("gzip", "extra-samples")]
    [InlineData("zlib", "extra-samples")]
    [InlineData("gzip", "truncated-trailer")]
    [InlineData("zlib", "truncated-trailer")]
    [InlineData("gzip", "damaged-trailer")]
    [InlineData("zlib", "damaged-trailer")]
    public async Task ReadSubsetAsync_InvalidEncodedChunk_RejectsInsteadOfReturningExpectedPrefix(string codec, string damage)
    {
        var objects = BuildRaw();
        var raw = objects[Root + "/0.0"];
        if (damage == "extra-samples")
        {
            raw = [.. raw, 1, 2, 3, 4];
        }
        var encoded = Encode(codec, raw);
        if (damage == "empty")
        {
            encoded = [];
        }
        else if (damage == "truncated-trailer")
        {
            encoded = encoded[..^(codec == "gzip" ? 8 : 4)];
        }
        else if (damage == "damaged-trailer")
        {
            encoded[^1] ^= 0xff;
        }
        SetCodec(objects, codec);
        objects[Root + "/0.0"] = encoded;
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", Root);
        var act = () => new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", Root, metadata,
            new ZarrSubsetRequest { Variable = "integrity", Start = [0, 0], Stop = [2, 2] });

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("none")]
    [InlineData("gzip")]
    [InlineData("zlib")]
    public async Task ReadSubsetAsync_ValidEncodedEdgeChunk_PreservesInBoundsSamples(string codec)
    {
        // Full-sized chunks may include padding beyond a non-divisible array edge.
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 3, 3, 2, 2, (r, c) => r * 3 + c + 1);
        SetCodec(objects, codec);
        foreach (var key in objects.Keys.Where(key => !key.EndsWith(".zarray", StringComparison.Ordinal)).ToArray())
        {
            objects[key] = Encode(codec, objects[key]);
        }
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", Root);
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", Root, metadata,
            new ZarrSubsetRequest { Variable = "integrity", Start = [2, 2], Stop = [3, 3] });

        BitConverter.ToSingle(subset.Data).Should().Be(9);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("gzip", "short-header")]
    [InlineData("gzip", "short-body")]
    [InlineData("gzip", "bad-crc")]
    [InlineData("gzip", "bad-size")]
    [InlineData("gzip", "junk")]
    [InlineData("gzip", "reserved-flags")]
    [InlineData("zlib", "short-header")]
    [InlineData("zlib", "short-body")]
    [InlineData("zlib", "junk")]
    public async Task ReadSubsetAsync_InvalidContainer_RejectsAllEncodedData(string codec, string damage)
    {
        var encoded = Encode(codec, BuildRaw()[Root + "/0.0"]);
        switch (damage)
        {
            case "short-header": encoded = encoded[..1]; break;
            case "short-body": encoded = encoded[..(codec == "gzip" ? 12 : 4)]; break;
            case "bad-crc": encoded[^8] ^= 1; break;
            case "bad-size": encoded[^4] ^= 1; break;
            case "junk": encoded = [.. encoded, 0xff, 0]; break;
            case "reserved-flags": encoded[3] |= 0xe0; break;
        }
        var act = () => ReadEncoded(codec, encoded);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadSubsetAsync_GzipOptionalFieldsAndHeaderCrc_ValidatesHeaderBeforeSamples(bool damageCrc)
    {
        // Independent Python gzip/struct/binascii producer: float32 [1,2,3,4],
        // FEXTRA with an empty AB subfield, FNAME, FCOMMENT, and FHCRC.
        var encoded = Convert.FromBase64String("H4sIHgAAAAACAwQAQUIAAGZpeHR1cmUASG9udWEAZOxjYGiwZ2BgcAAiIG5wAABUFKeLEAAAAA==");
        if (damageCrc)
        {
            encoded[30] ^= 1;
            var act = () => ReadEncoded("gzip", encoded);
            await act.Should().ThrowAsync<InvalidDataException>();
        }
        else
        {
            (await ReadEncoded("gzip", encoded)).Data.Should().Equal(BuildRaw()[Root + "/0.0"]);
        }
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadSubsetAsync_ConcatenatedGzipMembers_PreservesCompleteSamples(bool emptyFirstMember)
    {
        var raw = BuildRaw()[Root + "/0.0"];
        var first = Encode("gzip", emptyFirstMember ? [] : raw[..8]);
        var second = Encode("gzip", emptyFirstMember ? raw : raw[8..]);
        var subset = await ReadEncoded("gzip", [.. first, .. second]);
        subset.Data.Should().Equal(raw);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("gzip")]
    [InlineData("zlib")]
    public async Task ReadSubsetAsync_EmptyDecodedStreamForNonemptyChunk_Rejects(string codec)
    {
        var act = () => ReadEncoded(codec, Encode(codec, []));
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadSubsetAsync_ExcessiveGzipMemberCount_RejectsAsResourceLimit()
    {
        var member = Encode("gzip", []);
        var encoded = Enumerable.Range(0, 4097).SelectMany(_ => member).Concat(Encode("gzip", BuildRaw()[Root + "/0.0"])).ToArray();
        var act = () => ReadEncoded("gzip", encoded);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*member*limit*");
    }

    private static async Task<ZarrSubsetResult> ReadEncoded(string codec, byte[] encoded)
    {
        var objects = BuildRaw();
        SetCodec(objects, codec);
        objects[Root + "/0.0"] = encoded;
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", Root);
        return await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", Root, metadata,
            new ZarrSubsetRequest { Variable = "integrity", Start = [0, 0], Stop = [2, 2] });
    }

    private static Dictionary<string, byte[]> BuildRaw() =>
        ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 2, 2, 2, 2, (r, c) => r * 2 + c + 1);

    private static void SetCodec(Dictionary<string, byte[]> objects, string codec)
    {
        var document = JsonNode.Parse(objects[Root + "/.zarray"])!;
        document["compressor"] = codec == "none" ? null : JsonNode.Parse("{\"id\":\"" + codec + "\"}");
        objects[Root + "/.zarray"] = Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    private static byte[] Encode(string codec, byte[] raw)
    {
        if (codec == "none")
        {
            return raw;
        }
        if (raw.Length == 0)
        {
            // Independent Python gzip.compress(b'', mtime=0) / zlib.compress(b'') fixtures.
            // Disposing an unwritten .NET encoder can emit no bytes at all.
            return Convert.FromBase64String(codec == "gzip" ? "H4sIAAAAAAACAwMAAAAAAAAAAAA=" : "eJwDAAAAAAE=");
        }
        using var output = new MemoryStream();
        using (Stream encoder = codec == "gzip"
                   ? new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)
                   : new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            encoder.Write(raw);
        }
        return output.ToArray();
    }
}
