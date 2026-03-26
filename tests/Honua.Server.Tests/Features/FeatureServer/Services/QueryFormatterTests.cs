// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

public sealed class QueryFormatterTests
{
    [Fact]
    public async Task FormatQueryResultAsync_WithOutputSrid_SetsFeatureGeometrySpatialReference()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);

        var result = QueryResult<Feature>.Create(
            1,
            [
                Feature.Create(
                    1,
                    CreatePointGeometry(1, 2, 4326),
                    ImmutableDictionary<string, object?>.Empty.Add("name", "alpha"))
            ]);

        var (response, contentType) = await formatter.FormatQueryResultAsync(
            result,
            CreatePointLayer(),
            format: "json",
            returnGeometry: true,
            outputSrid: 3857,
            returnZ: true,
            returnM: true,
            geometryPrecision: null,
            maxAllowableOffset: null);

        contentType.Should().Be("application/json");
        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.SpatialReference?.Wkid.Should().Be(3857);
        queryResponse.Features.Should().HaveCount(1);
        queryResponse.Features[0].Geometry?.SpatialReference?.Wkid.Should().Be(3857);
    }

    private static LayerDefinition CreatePointLayer()
        => new(
            7,
            "test-layer",
            null,
            Honua.Core.Features.Catalog.Domain.GeometryType.Point,
            SpatialReference.WGS84,
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ]);

    private static byte[] CreatePointGeometry(double x, double y, int srid)
    {
        var writer = new WKBWriter();
        var point = new Point(x, y) { SRID = srid };
        return writer.Write(point);
    }
}
