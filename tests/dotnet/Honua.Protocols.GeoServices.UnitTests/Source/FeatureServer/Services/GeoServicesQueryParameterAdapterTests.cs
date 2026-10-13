// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class GeoServicesQueryParameterAdapterTests
{
    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public async Task ConvertAsync_WithoutResultRecordCount_UsesAdvertisedMaxRecordCount()
    {
        var adapter = new GeoServicesQueryParameterAdapter(NullLogger<GeoServicesQueryParameterAdapter>.Instance);
        var request = new GeoServicesQueryRequest
        {
            Parameters = new QueryParameters { F = "json" },
            QueryLimits = new QueryLimits
            {
                MaxRecordCount = 10000,
                DefaultRecordCount = 1000
            }
        };
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };

        var result = await adapter.ConvertAsync(request, resource);

        result.IsSuccess.Should().BeTrue();
        result.Query.Should().NotBeNull();
        var query = result.Query!.Value;
        query.Limit.Should().Be(10000,
            "the page must match the layer's advertised maxRecordCount when the caller omits resultRecordCount");
    }

    [UnitTest]
    public async Task ConvertAsync_TrueCurve_HonorsConfiguredVertexBudget()
    {
        const string fullSweepArc = "{\"a\":[[1,0],[0,0],0,0]}";
        var curve = GeoServicesGeometryConverter.ParseCurveGeometry(
            $"{{\"curvePaths\":[[[1,0],{string.Join(',', Enumerable.Repeat(fullSweepArc, 500))}]]}}");
        var request = new GeoServicesQueryRequest
        {
            Parameters = new QueryParameters { F = "json" },
            ParsedGeometry = curve,
            QueryLimits = new QueryLimits
            {
                MaxRecordCount = 1000,
                DefaultRecordCount = 100
            }
        };
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger }
            ]
        };

        var admitted = new GeoServicesQueryParameterAdapter(
            NullLogger<GeoServicesQueryParameterAdapter>.Instance,
            Options.Create(new LimitsOptions
            {
                Geometry = new GeometryLimits { MaxVerticesPerGeometry = 100_000 }
            }));
        var admittedResult = await admitted.ConvertAsync(request, resource);

        admittedResult.IsSuccess.Should().BeTrue();
        var geometry = new WKBReader().Read(admittedResult.Query!.Value.SpatialFilter!.Value.Geometry);
        geometry.NumPoints.Should().BeGreaterThan(50_000);

        var capped = new GeoServicesQueryParameterAdapter(
            NullLogger<GeoServicesQueryParameterAdapter>.Instance,
            Options.Create(new LimitsOptions
            {
                Geometry = new GeometryLimits { MaxVerticesPerGeometry = 50_000 }
            }));
        var cappedResult = await capped.ConvertAsync(request, resource);

        cappedResult.IsSuccess.Should().BeFalse();
    }

    [UnitTest]
    public async Task ConvertAsync_OmittedDistanceUnits_UsesGeometrySpatialReferenceLinearUnit()
    {
        var adapter = new GeoServicesQueryParameterAdapter(
            NullLogger<GeoServicesQueryParameterAdapter>.Instance,
            geographicSridClassifier: new FixedLinearUnitClassifier(2263, CrsLinearUnitFactor.UsSurveyFootMeters));
        var request = DistanceRequest(2263, units: null);
        var resource = PointResource();

        var result = await adapter.ConvertAsync(request, resource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var filter = result.Query!.Value.SpatialFilter!.Value;
        filter.Distance.Should().BeApproximately(500d * CrsLinearUnitFactor.UsSurveyFootMeters, 1e-9);
        filter.DistanceUnit.Should().Be(DistanceUnit.Meters);
    }

    [UnitTest]
    public async Task ConvertAsync_ExplicitMetres_IgnoresGeometryLinearUnit()
    {
        var adapter = new GeoServicesQueryParameterAdapter(
            NullLogger<GeoServicesQueryParameterAdapter>.Instance,
            geographicSridClassifier: new FixedLinearUnitClassifier(2263, CrsLinearUnitFactor.UsSurveyFootMeters));
        var request = DistanceRequest(2263, units: "esriSRUnit_Meter");

        var result = await adapter.ConvertAsync(request, PointResource());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var filter = result.Query!.Value.SpatialFilter!.Value;
        filter.Distance.Should().Be(500);
        filter.DistanceUnit.Should().Be(DistanceUnit.Meters);
    }

    [UnitTest]
    public async Task ConvertAsync_GeographicDistanceWithoutUnits_StaysInMetres()
    {
        var adapter = new GeoServicesQueryParameterAdapter(
            NullLogger<GeoServicesQueryParameterAdapter>.Instance,
            geographicSridClassifier: new FixedLinearUnitClassifier(2263, CrsLinearUnitFactor.UsSurveyFootMeters));
        var request = DistanceRequest(4326, units: null);

        var result = await adapter.ConvertAsync(request, PointResource());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Query!.Value.SpatialFilter!.Value.Distance.Should().Be(500);
    }

    [UnitTest]
    public async Task ConvertAsync_CancelledDistanceUnitLookup_PropagatesCancellation()
    {
        var adapter = new GeoServicesQueryParameterAdapter(
            NullLogger<GeoServicesQueryParameterAdapter>.Instance,
            geographicSridClassifier: new CancelledLinearUnitClassifier());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => adapter.ConvertAsync(DistanceRequest(2263, units: null), PointResource(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static GeoServicesQueryRequest DistanceRequest(int inputSrid, string? units)
        => new()
        {
            Parameters = new QueryParameters
            {
                F = "json",
                Distance = 500,
                Units = units,
                SpatialRel = "esriSpatialRelIntersects"
            },
            ParsedGeometry = new GeoServicesGeometry { X = 0, Y = 0 },
            InputSrid = inputSrid,
            QueryLimits = new QueryLimits
            {
                MaxRecordCount = 1000,
                DefaultRecordCount = 100
            }
        };

    private static MetadataV2Resource PointResource()
        => new()
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = new MetadataV2SpatialReference { Srid = 2263 },
                GeometryType = MetadataV2GeometryType.Point
            }
        };

    private sealed class FixedLinearUnitClassifier(int projectedSrid, double metersPerUnit) : IGeographicSridClassifier
    {
        public ValueTask<bool> IsGeographicAsync(int srid, CancellationToken cancellationToken = default)
            => new(srid != projectedSrid);

        public ValueTask<bool> IsGeographicForMeasurementAsync(int srid, CancellationToken cancellationToken = default)
            => new(srid != projectedSrid);

        public ValueTask<double?> TryResolveLinearUnitFactorAsync(int srid, CancellationToken cancellationToken = default)
            => new(srid == projectedSrid ? metersPerUnit : null);
    }

    private sealed class CancelledLinearUnitClassifier : IGeographicSridClassifier
    {
        public ValueTask<bool> IsGeographicAsync(int srid, CancellationToken cancellationToken = default)
            => new(false);

        public ValueTask<bool> IsGeographicForMeasurementAsync(int srid, CancellationToken cancellationToken = default)
            => new(false);

        public ValueTask<double?> TryResolveLinearUnitFactorAsync(int srid, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException(cancellationToken);
    }
}
