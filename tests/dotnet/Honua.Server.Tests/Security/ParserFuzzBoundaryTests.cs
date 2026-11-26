// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Infrastructure.Scene;
using Honua.Server.Features.Styling.Sld;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Security;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ParserFuzzBoundaryTests
{
    [UnitTheory]
    [MemberData(nameof(MalformedWkbPayloads))]
    public async Task WkbFormatReader_MalformedOrBoundaryPayload_RejectsCleanly(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        Func<Task> act = async () =>
        {
            var count = 0;
            await foreach (var _ in WkbFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
            {
                count++;
            }

            count.Should().BeGreaterThanOrEqualTo(0);
        };

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    public static IEnumerable<object[]> MalformedWkbPayloads()
    {
        yield return [new byte[] { 0x01 }];
        yield return [new byte[] { 0xFF, 0x01, 0x00, 0x00, 0x00 }];
        yield return [new byte[] { 0x01, 0x99, 0x99, 0x99, 0x99, 0x00, 0x00, 0x00, 0x00 }];
        yield return [new byte[] { 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03 }];
        // LineString claiming 0x7FFFFFFF points with zero trailing coordinates.
        yield return [new byte[] { 0x01, 0x02, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x7F }];
        // Polygon claiming 0x7FFFFFFF rings with zero trailing ring data.
        yield return [new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x7F }];
    }

    [UnitTheory]
    [InlineData("{")]
    [InlineData("""{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[1]}}]}""")]
    [InlineData("""{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"BogusType","coordinates":[0,0]}}]}""")]
    [InlineData("""{"type":"FeatureCollection","features":"not-an-array"}""")]
    [InlineData("not json at all")]
    public async Task StreamingGeoJsonReader_MalformedInput_RejectsWithoutUnhandledCrash(string payload)
    {
        var reader = new StreamingGeoJsonReader(new ImportLimits { MaxMemoryBytes = 1024 * 1024, MaxSingleFeatureBytes = 64 * 1024 });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var result = await reader.ValidateAsync(stream, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().NotBeEmpty();
    }

    [UnitTest]
    public async Task StreamingGeoJsonReader_DeeplyNestedJson_RejectsCleanly()
    {
        var nested = new string('[', 200) + new string(']', 200);
        var payload = $"{{\"type\":\"FeatureCollection\",\"features\":[{{\"type\":\"Feature\",\"geometry\":null,\"properties\":{{\"a\":{nested}}}}}]}}";
        var reader = new StreamingGeoJsonReader(new ImportLimits { MaxMemoryBytes = 1024 * 1024, MaxSingleFeatureBytes = 64 * 1024 });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var result = await reader.ValidateAsync(stream, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a = 'unterminated")]
    [InlineData("a = 1 AND")]
    [InlineData("S_INTERSECTS(geom, POLYGON((0 0, 1 1)))")]
    [InlineData("a = 1; DROP TABLE users; --")]
    public void Cql2Parser_MalformedInput_ThrowsArgumentException(string expression)
    {
        Action act = () => new Cql2Parser().Parse(expression);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Cql2Parser_ExcessiveNesting_ThrowsArgumentExceptionWithoutStackOverflow()
    {
        var deep = new string('(', 400) + "a = 1" + new string(')', 400);

        Action act = () => new Cql2Parser().Parse(deep);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTheory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"op":"unknown_op","args":["a",1]}""")]
    [InlineData("""{"op":"=","args":[{"property":"a"}]}""")]
    [InlineData("""{"op":"and","args":"bad"}""")]
    public void Cql2JsonParser_MalformedInput_ThrowsArgumentException(string payload)
    {
        Action act = () => new Cql2JsonParser().Parse(payload);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Cql2JsonParser_ExcessiveNesting_ThrowsArgumentExceptionWithoutStackOverflow()
    {
        var builder = new StringBuilder();
        const int depth = 128;
        for (var i = 0; i < depth; i++)
        {
            builder.Append("""{"op":"not","args":[""");
        }

        builder.Append("""{"op":"=","args":[{"property":"a"},1]}""");
        for (var i = 0; i < depth; i++)
        {
            builder.Append("]}");
        }

        Action act = () => new Cql2JsonParser().Parse(builder.ToString());

        act.Should().Throw<ArgumentException>();
    }

    [UnitTheory]
    [InlineData("<not-closed")]
    [InlineData("<UnknownRoot />")]
    [InlineData("<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><StyledLayerDescriptor>&xxe;</StyledLayerDescriptor>")]
    [InlineData("<!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;\">]><StyledLayerDescriptor>&lol2;</StyledLayerDescriptor>")]
    public void SldParser_MalformedOrDtdXml_ThrowsSldParseException(string xml)
    {
        Action act = () => SldParser.Parse(xml);

        act.Should().Throw<SldParseException>();
    }

    [UnitTest]
    public void SldParser_OversizedInput_ThrowsSldParseException()
    {
        var oversized = "<StyledLayerDescriptor>" + new string('a', 1_048_600) + "</StyledLayerDescriptor>";

        Action act = () => SldParser.Parse(oversized);

        act.Should().Throw<SldParseException>().WithMessage("*character limit*");
    }

    [UnitTest]
    public async Task SceneTilesetArchiveImporter_CorruptZipBytes_ThrowsValidationException()
    {
        var importer = CreateSceneImporter();
        var request = new SceneTilesetArchiveImportRequest(Encoding.UTF8.GetBytes("not a zip archive"));

        var act = () => importer.ImportAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [UnitTheory]
    [InlineData("../escape.json")]
    [InlineData("nested/../../escape.json")]
    [InlineData("bad\0name.json")]
    [InlineData("malware.exe")]
    public async Task SceneTilesetArchiveImporter_MaliciousZipEntryPath_ThrowsValidationException(string entryName)
    {
        var importer = CreateSceneImporter();
        var zipBytes = BuildZip((entryName, "{}"));
        var request = new SceneTilesetArchiveImportRequest(zipBytes);

        var act = () => importer.ImportAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [UnitTest]
    public async Task SceneTilesetArchiveImporter_ZipBombExceedingUncompressedCap_ThrowsValidationException()
    {
        var importer = CreateSceneImporter();
        var repetitive = new string('A', 64 * 1024);
        var zipBytes = BuildZip(("tileset.json", $"{{\"asset\":{{\"version\":\"1.0\"}},\"pad\":\"{repetitive}\"}}"));
        var tightLimits = new SceneTilesetArchiveImportOptions(MaxUncompressedBytes: 4096, MaxSingleEntryBytes: 4096);
        var request = new SceneTilesetArchiveImportRequest(zipBytes, Limits: tightLimits);

        var act = () => importer.ImportAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    private static SceneTilesetArchiveImporter CreateSceneImporter()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var options = Options.Create(new SceneGenerationServerOptions());
        return new SceneTilesetArchiveImporter(env, options, NullLogger<SceneTilesetArchiveImporter>.Instance);
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }
}
