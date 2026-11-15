// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

public sealed class OgcCsvHeaderTests
{
    [UnitTheory]
    [InlineData("name", "id,name,geometry")]
    [InlineData("id_1,geometry_1", "id,id_1,geometry_1,geometry")]
    [InlineData("id,name", "id_1,id,name,geometry")]
    [InlineData("ID,id_1,id_2", "id_3,ID,id_1,id_2,geometry")]
    [InlineData("geometry,geometry_1", "id,geometry,geometry_1,geometry_2")]
    [InlineData("id,geometry", "id_1,id,geometry,geometry_1")]
    public async Task BuildCsvResponse_ReservedPropertyNames_PreservesDistinctColumns(
        string propertyNames, string expectedHeader)
    {
        var fields = propertyNames.Split(',');
        var values = fields.Select((_, index) => $"property-{index}").ToArray();
        GeoJsonFeature[] features = [new()
        {
            Id = 42,
            Properties = fields.Zip(values).ToDictionary(pair => pair.First, pair => (object?)pair.Second)
        }];
        var expected = expectedHeader + Environment.NewLine
            + "42," + string.Join(",", values) + "," + Environment.NewLine;

        Assert.Equal(expected, OgcResponseFormatter.BuildCsvResponse(features, fields));
        using var stream = new MemoryStream();
        await OgcResponseFormatter.StreamCsvResponseAsync(stream, Enumerate(features), new(fields));
        Assert.Equal(expected, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [UnitTest]
    public async Task BuildCsvResponse_EmptyResultsWithReservedProperties_EmitsDistinctHeaders()
    {
        var expected = "id_1,id,geometry,geometry_1" + Environment.NewLine;
        Assert.Equal(expected,
            OgcResponseFormatter.BuildCsvResponse([], ["id", "geometry"]));
        using var stream = new MemoryStream();
        await OgcResponseFormatter.StreamCsvResponseAsync(stream, Enumerate([]), new(["id", "geometry"]));
        Assert.Equal(expected, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static async IAsyncEnumerable<GeoJsonFeature> Enumerate(IEnumerable<GeoJsonFeature> features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }

        await Task.CompletedTask;
    }
}
