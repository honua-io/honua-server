// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Io.Export.Writers;
using Honua.Io.Export;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.Protocols.Ogc.Common;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportExport_LeadingWhitespaceBeforeHeader_PreservesSchemaAndData(bool hasGeometry)
    {
        const string wkt = "POINT ZM (-157.1234567890123 21.1234567890123 30.125 40.25)";
        var header = hasGeometry ? "WKT,note,missing" : "note,missing";
        var row = hasGeometry ? $"{wkt},   ," : "   ,";
        var source = Assert.Single(await ReadAsync($"\n   \n\t \n{header}\n{row}\n"));
        Assert.Equal("   ", Assert.IsType<string>(source.Attributes["note"]));
        Assert.False(source.Attributes.Exists("field_1"));
        var roundtrip = await ExportAndReadAsync(source);
        Assert.Equal("   ", Assert.IsType<string>(roundtrip.Attributes["note"]));
        Assert.False(roundtrip.Attributes.Exists("missing"));
        if (hasGeometry)
        {
            Assert.True(new WKTReader().Read(wkt).EqualsExact(roundtrip.Geometry));
            Assert.Equal(30.125, roundtrip.Geometry.Coordinate.Z);
            Assert.Equal(40.25, roundtrip.Geometry.Coordinate.M);
        }
        else
        {
            Assert.Null(roundtrip.Geometry);
        }
    }

    [Theory]
    [InlineData(false, "\"\"", "")]
    [InlineData(true, "\"\"", "")]
    [InlineData(false, "   ", "   ")]
    [InlineData(true, "   ", "   ")]
    [InlineData(false, "\"a, b\"", "a, b")]
    [InlineData(true, "\"a, b\"", "a, b")]
    public async Task ImportExport_ProtocolCsv_PreservesStringsAndNulls(bool wfs, string cell, string expected)
    {
        var source = Assert.Single(await ReadAsync($"note,missing\n{cell},\n"));
        string csv;
        if (wfs)
        {
            // Exercise the actual field formatter used by both WFS CSV response paths.
            var escape = typeof(Wfs20Handler).GetMethod("EscapeCsv", BindingFlags.NonPublic | BindingFlags.Static)!;
            var convert = typeof(Wfs20Handler).GetMethod("ConvertFieldValueToInvariantString", BindingFlags.NonPublic | BindingFlags.Static)!;
            var field = new MetadataV2Field { Name = "note", Type = MetadataV2FieldType.String, Nullable = true };
            var note = (string)escape.Invoke(null, [convert.Invoke(null, [source.Attributes["note"], field])])!;
            var missing = (string)escape.Invoke(null, [convert.Invoke(null, [null, field])])!;
            csv = $"note,missing\n{note},{missing}\n";
        }
        else
        {
            csv = OgcResponseFormatter.BuildCsvResponse([new GeoJsonFeature
            {
                Id = 1,
                Properties = new Dictionary<string, object?>
                {
                    ["note"] = source.Attributes["note"],
                    ["missing"] = null
                }
            }], ["note", "missing"]);
        }

        var roundtrip = Assert.Single(await ReadAsync(csv));
        Assert.Equal(expected, Assert.IsType<string>(roundtrip.Attributes["note"]));
        Assert.False(roundtrip.Attributes.Exists("missing"));
        Assert.Null(source.Geometry);
        Assert.Null(roundtrip.Geometry);
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
