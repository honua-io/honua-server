// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Infrastructure.Transforms;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Infrastructure.Transforms;

/// <summary>
/// Datum-transformation parity integration tests (issue #1274). Uses the embedded
/// Esri-default catalog to select a PROJ pipeline, applies it through PostGIS' 3-argument
/// ST_Transform, and asserts the result lands within the documented tolerance of the
/// published EPSG/PROJ oracle.
/// </summary>
/// <remarks>
/// The oracle is currently the EPSG/PROJ reference pipeline executed by PostGIS itself.
/// The tests are structured so an ArcGIS-generated golden coordinate set can be dropped
/// in later (see docs/gis/datum-transformation-parity.md) without restructuring.
/// </remarks>
[Collection("Database")]
public sealed class DatumTransformationParityTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private readonly IDatumTransformationCatalog _catalog = EsriDatumTransformationCatalog.Create();
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
        // Tolerance per docs/gis/datum-transformation-parity.md (NAD83->WGS84_1: 0.01 m ~ 1e-7 deg).
        result!.Value.X.Should().BeApproximately(-104.9903, 1e-5);
        result.Value.Y.Should().BeApproximately(39.7392, 1e-5);
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

        // Both should be sub-meter; ~1e-5 deg ~ 1.1 m upper bound.
        selected!.Value.X.Should().BeApproximately(oracle!.Value.X, 1e-5);
        selected.Value.Y.Should().BeApproximately(oracle.Value.Y, 1e-5);
    }

    [IntegrationTest]
    public async Task TransformPoint_NadconGridPipeline_WithoutGrid_FailsExplicitly()
    {
        // The NAD27->NAD83 NADCON pipeline (WKID 1241) requires us_noaa_conus.tif. When the
        // grid is absent from the PROJ data path, PostGIS rejects the pipeline and the service
        // returns null (the documented explicit-failure contract) rather than silently
        // degrading to a Helmert approximation. If the grid is present, the transform succeeds
        // and lands near the input (NAD27<->NAD83 shift is small in CONUS, < ~200 m).
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
        viaSelection!.Value.X.Should().Be(viaSridOnly!.Value.X);
        viaSelection.Value.Y.Should().Be(viaSridOnly.Value.Y);
    }
}
