// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;
using NetTopologySuite.IO.Esri;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class ShapefileNullRoundtripTests
{
    [Theory]
    [InlineData("unlocated,\nlocated,POINT (1.25 2.5)\n")]
    [InlineData("located,POINT (1.25 2.5)\nunlocated,\n")]
    [InlineData("unlocated,\nunlocated-too,\n")]
    public async Task ImportExport_NullGeometryRows_PreservesEveryRowAndAttribute(string rows)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes($"name,WKT\n{rows}"));
        var source = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(input, CancellationToken.None))
        {
            source.Add(feature);
        }

        Assert.Equal(2, source.Count);
        await using var output = new MemoryStream();
        var result = await ShapefileExportWriter.WriteAsync(output, Rows(source),
            [new ExportField("name", ExportFieldType.String, true)], ExportGeometryType.Point,
            null, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(source.Count, result.WrittenCount);
        Assert.Equal(0, result.SkippedNullGeometry);
        var scratch = Path.Join(Path.GetTempPath(), $"honua-null-roundtrip-{Guid.NewGuid():N}");
        try
        {
            output.Position = 0;
            using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
            zip.ExtractToDirectory(scratch);
            var roundtrip = Shapefile.ReadAllFeatures(Path.Join(scratch, "export.shp")).ToArray();
            Assert.Equal(source.Count, roundtrip.Length);
            var shapeBytes = await File.ReadAllBytesAsync(Path.Join(scratch, "export.shp"));
            var recordOffset = 100;
            for (var i = 0; i < source.Count; i++)
            {
                Assert.Equal(source[i].Attributes["name"], Assert.IsType<string>(roundtrip[i].Attributes["name"]));
                if (source[i].Geometry is null)
                {
                    Assert.True(roundtrip[i].Geometry is null || roundtrip[i].Geometry.IsEmpty);
                    Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(shapeBytes.AsSpan(recordOffset + 8, 4)));
                }
                else
                {
                    Assert.True(source[i].Geometry.EqualsExact(roundtrip[i].Geometry));
                    Assert.True(double.IsNaN(roundtrip[i].Geometry.Coordinate.Z));
                    Assert.True(double.IsNaN(roundtrip[i].Geometry.Coordinate.M));
                }

                recordOffset += 8 + 2 * BinaryPrimitives.ReadInt32BigEndian(shapeBytes.AsSpan(recordOffset + 4, 4));
            }

            Assert.Equal(shapeBytes.Length, recordOffset);
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_FieldNameLoss_IncludesWarningInDownload()
    {
        await using var output = new MemoryStream();
        var source = new NetTopologySuite.Features.Feature(new WKTReader().Read("POINT (1 2)"),
            new NetTopologySuite.Features.AttributesTable { { "descriptive_name", "retained value" } });
        var result = await ShapefileExportWriter.WriteAsync(output, Rows([source]),
            [new ExportField("descriptive_name", ExportFieldType.String, true)], ExportGeometryType.Point,
            null, NullLogger.Instance, CancellationToken.None);
        Assert.NotEmpty(result.Warnings);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        var entry = Assert.Single(zip.Entries, entry => entry.FullName == "export-warnings.txt");
        using var reader = new StreamReader(entry.Open());
        var warnings = await reader.ReadToEndAsync();
        Assert.Contains("descriptive_name", warnings, StringComparison.Ordinal);
        Assert.All(result.Warnings, warning => Assert.Contains(warning, warnings, StringComparison.Ordinal));
    }

    private static async IAsyncEnumerable<Feature> Rows(IEnumerable<NetTopologySuite.Features.IFeature> source)
    {
        var id = 0;
        foreach (var feature in source)
        {
            yield return Feature.Create(++id,
                feature.Geometry is null ? null : new WKBWriter().Write(feature.Geometry),
                feature.Attributes.GetNames().ToImmutableDictionary(name => name, name => (object?)feature.Attributes[name]));
        }

        await Task.CompletedTask;
    }
}
