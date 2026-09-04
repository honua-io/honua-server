// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using NetTopologySuite.IO;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class CsvOrdinateRoundtripTests
{
    [Theory]
    [InlineData("POINT (-157.1234567890123 21.1234567890123)")]
    [InlineData("POINT Z (-157.1234567890123 21.1234567890123 30.1234567890123)")]
    [InlineData("POINT M (1 2 40.1234567890123)")]
    [InlineData("POINT ZM (1 2 30.1234567890123 40.1234567890123)")]
    [InlineData("LINESTRING ZM (1 2 -30 40, 5 6 70 -80)")]
    [InlineData("MULTILINESTRING Z ((0 0 30, 1 0 40), (10 0 50, 11 0 60))")]
    [InlineData("POLYGON ZM ((0 0 1 10, 2 0 2 20, 2 2 3 30, 0 0 1 10))")]
    public async Task ImportExport_DimensionalWkt_PreservesAllOrdinatesAndValues(string wkt)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            $"name,amount,WKT\nHawaiʻi 東京,123456789.123456789,\"{wkt}\"\n"));
        var imported = await ReadAsync(input);
        var source = Assert.Single(imported);
        await using var output = new MemoryStream();
        Assert.Equal(1, await CsvExportWriter.WriteAsync(output, Rows(Feature.Create(1,
            new WKBWriter(ByteOrder.LittleEndian, false, true, true).Write(source.Geometry),
            source.Attributes.GetNames().ToImmutableDictionary(name => name, name => (object?)source.Attributes[name]))),
            [new ExportField("name", ExportFieldType.String, true), new ExportField("amount", ExportFieldType.String, true)],
            CancellationToken.None));
        output.Position = 0;
        var roundtrip = Assert.Single(await ReadAsync(output));
        Assert.Equal(source.Geometry.GeometryType, roundtrip.Geometry.GeometryType);
        Assert.Equal(source.Geometry.NumGeometries, roundtrip.Geometry.NumGeometries);
        Assert.Equal(source.Geometry.NumPoints, roundtrip.Geometry.NumPoints);
        Assert.True(source.Geometry.EqualsExact(roundtrip.Geometry));
        for (var i = 0; i < source.Geometry.NumPoints; i++)
        {
            var expected = source.Geometry.Coordinates[i];
            var actual = roundtrip.Geometry.Coordinates[i];
            Assert.Equal(expected.X, actual.X);
            Assert.Equal(expected.Y, actual.Y);
            Assert.Equal(expected.Z, actual.Z);
            Assert.Equal(expected.M, actual.M);
        }

        Assert.Equal("Hawaiʻi 東京", Assert.IsType<string>(roundtrip.Attributes["name"]));
        Assert.Equal("123456789.123456789", Assert.IsType<string>(roundtrip.Attributes["amount"]));
    }

    private static async Task<List<NetTopologySuite.Features.IFeature>> ReadAsync(Stream stream)
    {
        var result = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            result.Add(feature);
        }

        return result;
    }

    private static async IAsyncEnumerable<Feature> Rows(Feature feature)
    {
        yield return feature;
        await Task.CompletedTask;
    }
}
