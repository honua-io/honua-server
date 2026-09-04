// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using StoredFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class CsvAttributeRoundtripTests
{
    [Theory]
    [InlineData("\"\"", "")]
    [InlineData("\"   \"", "   ")]
    [InlineData("   ", "   ")]
    [InlineData("\" \t \"", " \t ")]
    [InlineData("\"Hawaiʻi, \"\"quoted\"\"\"", "Hawaiʻi, \"quoted\"")]
    [InlineData("\"first\nsecond\"", "first\nsecond")]
    public async Task ImportExport_ExplicitAttributeValues_PreserveExactStringsAndNulls(string cell, string expected)
    {
        var input = $"WKT,note,missing\nPOINT ZM (-157.1234567890123 21.1234567890123 30.125 40.25),{cell},\n";
        var source = Assert.Single(await ReadAsync(input));
        Assert.Equal(expected, Assert.IsType<string>(source.Attributes["note"]));
        Assert.False(source.Attributes.Exists("missing"));
        var roundtrip = await ExportAndReadAsync(source);
        Assert.Equal(expected, Assert.IsType<string>(roundtrip.Attributes["note"]));
        Assert.False(roundtrip.Attributes.Exists("missing"));
        Assert.True(source.Geometry.EqualsExact(roundtrip.Geometry));
        Assert.Equal(30.125, roundtrip.Geometry.Coordinate.Z);
        Assert.Equal(40.25, roundtrip.Geometry.Coordinate.M);
    }

    [Theory]
    [InlineData("\"\"", "")]
    [InlineData("   ", "   ")]
    public async Task ImportExport_AttributeOnlyRow_PreservesRow(string cell, string expected)
    {
        var source = Assert.Single(await ReadAsync($"note\n{cell}\n"));
        var roundtrip = await ExportAndReadAsync(source);
        Assert.Null(roundtrip.Geometry);
        Assert.Equal(expected, Assert.IsType<string>(roundtrip.Attributes["note"]));
        Assert.False(roundtrip.Attributes.Exists("missing"));
    }

    private static async Task<IFeature> ExportAndReadAsync(IFeature source)
    {
        using var output = new MemoryStream();
        Assert.Equal(1, await CsvExportWriter.WriteAsync(output, Rows(source),
            [new("note", ExportFieldType.String, true), new("missing", ExportFieldType.String, true)], CancellationToken.None));
        return Assert.Single(await ReadAsync(Encoding.UTF8.GetString(output.ToArray())));
    }

    private static async Task<List<IFeature>> ReadAsync(string csv)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var features = new List<IFeature>();
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }

    private static async IAsyncEnumerable<StoredFeature> Rows(IFeature source)
    {
        yield return StoredFeature.Create(1,
            source.Geometry is null ? null : new WKBWriter(ByteOrder.LittleEndian, false, true, true).Write(source.Geometry),
            source.Attributes.GetNames().ToImmutableDictionary(name => name, name => (object?)source.Attributes[name]));
        await Task.CompletedTask;
    }
}
