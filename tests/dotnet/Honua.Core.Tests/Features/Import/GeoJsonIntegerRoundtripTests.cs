// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Xunit;

namespace Honua.Core.Tests.Features.Import;

public sealed class GeoJsonIntegerRoundtripTests
{
    [Theory]
    [InlineData(9007199254740993L)]
    [InlineData(9007199254740992L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-9007199254740993L)]
    [InlineData(42L)]
    public async Task ImportExport_Int64PropertiesAndFeatureIds_PreserveExactTypesAndValues(long value)
    {
        var number = value.ToString(CultureInfo.InvariantCulture);
        var json = $$"""
            {"type":"FeatureCollection","features":[{"type":"Feature","id":{{number}},"geometry":{"type":"Point","coordinates":[-157.1234567890123,21.1234567890123,30.25]},"properties":{"key":{{number}},"fraction":1.25,"active":true,"name":"Hawaiʻi"}}]}
            """;
        var source = Assert.Single(await ReadAsync(json));
        Assert.Equal(value, Assert.IsType<long>(source.Attributes["key"]));
        Assert.Equal(value, Assert.IsType<long>(source.Attributes["id"]));
        var exported = new GeoJsonWriter().Write(source);
        var roundtrip = Assert.Single(await ReadAsync(exported));
        Assert.Equal(value, Assert.IsType<long>(roundtrip.Attributes["key"]));
        Assert.Equal(value, Assert.IsType<long>(roundtrip.Attributes["id"]));
        Assert.Equal(1.25, Assert.IsType<double>(roundtrip.Attributes["fraction"]));
        Assert.True(Assert.IsType<bool>(roundtrip.Attributes["active"]));
        Assert.Equal("Hawaiʻi", Assert.IsType<string>(roundtrip.Attributes["name"]));
        Assert.Equal(source.Geometry.GeometryType, roundtrip.Geometry.GeometryType);
        Assert.Equal(-157.1234567890123, roundtrip.Geometry.Coordinate.X);
        Assert.Equal(21.1234567890123, roundtrip.Geometry.Coordinate.Y);
        Assert.Equal(30.25, roundtrip.Geometry.Coordinate.Z);
        Assert.True(double.IsNaN(roundtrip.Geometry.Coordinate.M));
    }

    private static async Task<List<IFeature>> ReadAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var features = new List<IFeature>();
        await foreach (var feature in new StreamingGeoJsonReader().ReadFeaturesAsync(stream))
        {
            features.Add(feature);
        }

        return features;
    }
}
