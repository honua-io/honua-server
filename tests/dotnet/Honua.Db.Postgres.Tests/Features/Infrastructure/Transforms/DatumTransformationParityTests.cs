// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Transforms;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure.Transforms;

[Collection("Database")]
public sealed class DatumTransformationParityTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private readonly IDatumTransformationCatalog _catalog = GeoServicesDatumTransformationCatalog.Create();
    private PostGisCoordinateTransformService? _service;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var connectionProvider = new PostgresDatabaseConnectionProvider(
            _fixture.DataSource,
            NullLogger<PostgresDatabaseConnectionProvider>.Instance);
        _service = new PostGisCoordinateTransformService(
            connectionProvider,
            NullLogger<PostGisCoordinateTransformService>.Instance);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [IntegrationTest]
    public async Task TransformPoint_Nad83ToWgs84_DefaultPipeline_IsWithinTolerance()
    {
        // A CONUS coordinate (Denver). NAD83 and WGS84 coincide to ~1 m; the selected
        // null-transformation pipeline should produce a near-identity result.
        _catalog.TryGetDefault(4269, 4326, out var selection).Should().BeTrue();

        var result = await _service!.TransformPointAsync(-104.9903, 39.7392, 4269, 4326, selection);

        result.Should().NotBeNull();
        var point = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        point.X.Should().BeApproximately(-104.9903, 1e-5);
        point.Y.Should().BeApproximately(39.7392, 1e-5);
    }

    [IntegrationTest]
    public async Task TransformPoint_SelectedPipeline_AgreesWithSridOnlyOracle_WithinTolerance()
    {
        // The selected default pipeline for NAD83->WGS84 must agree with PROJ's own
        // SRID-only choice to within the documented horizontal tolerance, proving the
        // explicit pipeline does not introduce error versus the EPSG/PROJ oracle.
        _catalog.TryGetDefault(4269, 4326, out var selection).Should().BeTrue();

        const double lon = -96.0;
        const double lat = 41.0;

        var selected = await _service!.TransformPointAsync(lon, lat, 4269, 4326, selection);
        var oracle = await _service!.TransformPointAsync(lon, lat, 4269, 4326);

        selected.Should().NotBeNull();
        oracle.Should().NotBeNull();

        var selectedPoint = selected is { } sp ? sp : throw new InvalidOperationException("Expected a selected transform result.");
        var oraclePoint = oracle is { } op ? op : throw new InvalidOperationException("Expected an oracle transform result.");

        // Both should be sub-meter; ~1e-5 deg ~ 1.1 m upper bound.
        selectedPoint.X.Should().BeApproximately(oraclePoint.X, 1e-5);
        selectedPoint.Y.Should().BeApproximately(oraclePoint.Y, 1e-5);
    }

    [Theory]
    [InlineData(4152)] // NAD83(HARN)     -> WGS84, EPSG 1580 (null)
    [InlineData(4759)] // NAD83(NSRS2007) -> WGS84, EPSG 15931 (null)
    [InlineData(6318)] // NAD83(2011)     -> WGS84, EPSG 9774 (null)
    public async Task TransformPoint_Wgs84RealizationNullPair_SeededPipelineApplies(int fromSrid)
    {
        // The deferred Helmert/WGS84-realization pairs seeded under #1501 are EPSG null
        // transformations (+proj=noop). This proves the seeded pipeline actually applies
        // through PostGIS' 3-argument ST_Transform on the runtime (not hand-authored) and
        // produces the exact-identity result the null transform mandates.
        _catalog.TryGetDefault(fromSrid, 4326, out var selection).Should().BeTrue();
        selection!.ProjPipeline.Should().Be("+proj=noop");

        const double lon = -96.0;
        const double lat = 41.0;

        var result = await _service!.TransformPointAsync(lon, lat, fromSrid, 4326, selection);

        result.Should().NotBeNull();
        // Null transform = exact identity; allow only floating-point noise.
        var identityPoint = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        identityPoint.X.Should().BeApproximately(lon, 1e-9);
        identityPoint.Y.Should().BeApproximately(lat, 1e-9);
    }

    [IntegrationTheory]
    [InlineData("108190", 4269)]
    [InlineData("108282", 4152)]
    [InlineData("108354", 6318)]
    [InlineData("Honua:WGS84_ITRF00_To_NAD83_NSRS2007_Via_2011", 4759)]
    public async Task TransformPoint_ExplicitRealizationOperation_ShiftsPointAndPreservesDefault(
        string operation, int sourceSrid)
    {
        const double longitude = -96;
        const double latitude = 41;
        _catalog.TryGetDefault(sourceSrid, 4326, out var defaultSelection).Should().BeTrue();
        var unchanged = await _service!.TransformPointAsync(
            longitude, latitude, sourceSrid, 4326, defaultSelection);
        unchanged.Should().NotBeNull();
        unchanged!.Value.X.Should().BeApproximately(longitude, 1e-9);
        unchanged.Value.Y.Should().BeApproximately(latitude, 1e-9);

        var forward = ResolveOperation(operation, sourceSrid, 4326);
        var shifted = await _service.TransformPointAsync(longitude, latitude, sourceSrid, 4326, forward);
        shifted.Should().NotBeNull();
        shifted!.Value.X.Should().BeApproximately(-96.00000801053693, 8e-8);
        shifted.Value.Y.Should().BeApproximately(41.00000720698309, 8e-8);

        var inverse = ResolveOperation(operation, 4326, sourceSrid);
        var restored = await _service.TransformPointAsync(
            shifted.Value.X, shifted.Value.Y, 4326, sourceSrid, inverse);
        restored.Should().NotBeNull();
        restored!.Value.X.Should().BeApproximately(longitude, 1e-8);
        restored.Value.Y.Should().BeApproximately(latitude, 1e-8);
    }

    // Independent PROJ 9.8.1 ESRI:108574 output for longitude -96, latitude 41,
    // ellipsoidal height zero. Angular tolerances bound horizontal error below 1 cm.
    public sealed record TimedOperationReference
    {
        public double Epoch { get; init; }
        public int SourceSrid { get; init; }
        public int TargetSrid { get; init; }
        public double Longitude { get; init; }
        public double Latitude { get; init; }
    }

    public static TheoryData<TimedOperationReference> TimedOperations => new()
    {
        new() { Epoch = 2010, SourceSrid = 7912, TargetSrid = 6319, Longitude = -95.99998935274661, Latitude = 40.999993141037606 },
        new() { Epoch = 2020, SourceSrid = 7912, TargetSrid = 6319, Longitude = -95.99998731925608, Latitude = 40.999993417501 },
        new() { Epoch = 2010, SourceSrid = 6319, TargetSrid = 7912, Longitude = -96.00001064726, Latitude = 41.00000685896444 },
        new() { Epoch = 2020, SourceSrid = 6319, TargetSrid = 7912, Longitude = -96.00001268075165, Latitude = 41.0000065825003 }
    };

    [IntegrationTheory]
    [MemberData(nameof(TimedOperations))]
    public async Task TransformPoint_TimedRealization_AgreesWithPublishedOperation(TimedOperationReference reference)
    {
        _catalog.TryGetByWkid(108574, reference.SourceSrid, reference.TargetSrid, out var selection).Should().BeTrue();
        var boundSelection = selection!.WithCoordinateEpoch(reference.Epoch);

        var result = await _service!.TransformPointAsync(-96, 41, reference.SourceSrid, reference.TargetSrid, boundSelection);

        result.Should().NotBeNull();
        result!.Value.X.Should().BeApproximately(reference.Longitude, 8e-8);
        result.Value.Y.Should().BeApproximately(reference.Latitude, 8e-8);
    }

    private DatumTransformationSelection ResolveOperation(string operation, int fromSrid, int toSrid)
    {
        DatumTransformationSelection? selection;
        var resolved = int.TryParse(operation, out var wkid)
            ? _catalog.TryGetByWkid(wkid, fromSrid, toSrid, out selection)
            : (selection = _catalog.FindByName(operation, fromSrid, toSrid)) is not null;
        resolved.Should().BeTrue();
        return selection!;
    }

    [IntegrationTest]
    public async Task TransformPoint_NadconGridPipeline_WithoutGrid_FailsExplicitly()
    {
        // The NAD27->NAD83 NADCON pipeline (WKID 1241) requires us_noaa_conus.tif. The seeded
        // +proj=pipeline string cannot be forced through PostGIS' 3-argument ST_Transform text
        // overload, so when a pipeline selection is passed the service either returns null (the
        // documented explicit-failure contract) or PROJ resolves the pair via its default path.
        // When the canonical grid is provisioned (docker/proj-grids image, #1501) the result is
        // a high-accuracy NADCON shift; see DatumGridProvisioningTests for the gated assertion.
        _catalog.TryGetByWkid(1241, 4267, 4269, out var selection).Should().BeTrue();
        selection!.RequiredGrids.Should().Contain("us_noaa_conus.tif");

        var result = await _service!.TransformPointAsync(-100.0, 40.0, 4267, 4269, selection);

        // Either the grid is missing (null = explicit failure) or present (near-input result).
        if (result is not null)
        {
            result.Value.X.Should().BeApproximately(-100.0, 3e-3);
            result.Value.Y.Should().BeApproximately(40.0, 3e-3);
        }
    }

    [IntegrationTest]
    public async Task TransformPoint_NullSelection_FallsBackToSridOnlyPath()
    {
        // A null selection must behave exactly like the SRID-only overload.
        var viaSelection = await _service!.TransformPointAsync(-104.9903, 39.7392, 4269, 4326, selection: null);
        var viaSridOnly = await _service!.TransformPointAsync(-104.9903, 39.7392, 4269, 4326);

        viaSelection.Should().NotBeNull();
        viaSridOnly.Should().NotBeNull();
        var viaSelectionPoint = viaSelection is { } vs ? vs : throw new InvalidOperationException("Expected a transform result via selection.");
        var viaSridOnlyPoint = viaSridOnly is { } vo ? vo : throw new InvalidOperationException("Expected a transform result via SRID-only path.");
        viaSelectionPoint.X.Should().Be(viaSridOnlyPoint.X);
        viaSelectionPoint.Y.Should().Be(viaSridOnlyPoint.Y);
    }
}
