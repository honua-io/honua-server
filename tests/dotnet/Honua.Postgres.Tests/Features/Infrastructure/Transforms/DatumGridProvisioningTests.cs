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
/// Grid-provisioning datum-fidelity tests (issue #1501). These prove that when the
/// canonical PROJ NADCON grid is present in the runtime PROJ data path — as provisioned
/// by the <c>docker/proj-grids</c> image — a grid-gated reprojection actually resolves
/// through the same shared transform path the query/import chokepoint uses, and lands
/// within the documented NAD27↔NAD83 CONUS bound with a real (non-identity) shift.
/// </summary>
/// <remarks>
/// <para>
/// The default Fast-tier fixture (<see cref="PostgresFixture"/>, <c>postgis/postgis:18-3.6</c>)
/// ships only the minimal Debian libproj-data subset and does NOT bundle the canonical
/// <c>us_noaa_conus.tif</c> grid, so these tests are gated behind <c>HONUA_PROJ_GRID_TEST</c>
/// and run only when pointed (via <c>HONUA_TEST_DB_URL</c>) at the grid-provisioned image
/// — see <c>docker/proj-grids/README.md</c>. Without the gate they skip, so the heavy grid
/// image is never built/pulled on the default path. The explicit-failure / default-path
/// contract (no grid present) stays covered by <see cref="DatumTransformationParityTests"/>.
/// </para>
/// <para>
/// Reference values measured against the runtime PROJ 9.6 (PostGIS 18-3.6) with
/// <c>us_noaa_conus.tif</c> baked in: NAD27 (-100, 40) → NAD83 ≈ (-100.0004056, 40.0000058);
/// NAD27 (-77, 38.9) → NAD83 ≈ (-76.9996986, 38.9001112). The grid shift is ~3–4e-4 deg
/// (~30–40 m), the expected CONUS magnitude for the NAD27↔NAD83 datum difference.
/// </para>
/// </remarks>
[Collection("Database")]
public sealed class DatumGridProvisioningTests : IAsyncLifetime
{
    private const string GridTestGateEnv = "HONUA_PROJ_GRID_TEST";

    private readonly PostgresFixture _fixture = new();
    private readonly IDatumTransformationCatalog _catalog = EsriDatumTransformationCatalog.Create();
    private PostGisCoordinateTransformService? _service;

    private static bool GridTestEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GridTestGateEnv));

    public async Task InitializeAsync()
    {
        if (!GridTestEnabled)
        {
            return;
        }

        await _fixture.InitializeAsync();
        var connectionProvider = new PostgresDatabaseConnectionProvider(
            _fixture.DataSource,
            NullLogger<PostgresDatabaseConnectionProvider>.Instance);
        _service = new PostGisCoordinateTransformService(
            connectionProvider,
            NullLogger<PostGisCoordinateTransformService>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (GridTestEnabled)
        {
            await _fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    public async Task TransformPoint_Nad27ToNad83_WithProvisionedConusGrid_AppliesNadconShiftWithinTolerance()
    {
        if (!GridTestEnabled)
        {
            // Skip unless explicitly pointed at the grid-provisioned image (docker/proj-grids).
            return;
        }

        // The catalog's NAD27 -> NAD83 default is the NADCON pipeline requiring us_noaa_conus.tif.
        _catalog.TryGetDefault(4267, 4269, out var selection).Should().BeTrue();
        selection!.RequiredGrids.Should().Contain("us_noaa_conus.tif");

        // Resolve through the same default (2-argument) path the chokepoint emits for a
        // grid-backed pipeline. With the canonical grid provisioned, PROJ resolves the
        // high-accuracy NADCON operation rather than failing or using a low-accuracy fallback.
        const double lon = -100.0;
        const double lat = 40.0;

        var result = await _service!.TransformPointAsync(lon, lat, 4267, 4269);

        result.Should().NotBeNull(
            "the provisioned us_noaa_conus.tif grid must let PROJ resolve the NAD27->NAD83 NADCON operation");
        var transformed = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");

        // It must be a real datum shift, not identity (NAD27 and NAD83 differ by ~30-40 m in CONUS).
        var shiftDegrees = Math.Sqrt(
            Math.Pow(transformed.X - lon, 2) + Math.Pow(transformed.Y - lat, 2));
        shiftDegrees.Should().BeGreaterThan(1e-4,
            "NAD27<->NAD83 is a real (non-identity) datum transformation in CONUS");

        // And it must stay within the NAD27<->NAD83 CONUS bound (well under ~0.01 deg / ~1 km).
        transformed.X.Should().BeApproximately(lon, 1e-3);
        transformed.Y.Should().BeApproximately(lat, 1e-3);

        // Pin the measured canonical-grid value so a regression to the low-accuracy bundled
        // fallback (which differs at the ~1e-4 deg level) is caught.
        transformed.X.Should().BeApproximately(-100.0004056, 5e-6);
        transformed.Y.Should().BeApproximately(40.0000058, 5e-6);
    }

    [IntegrationTest]
    public async Task TransformPoint_Nad27ToNad83_SecondConusPoint_MatchesProvisionedGridReference()
    {
        if (!GridTestEnabled)
        {
            return;
        }

        // A second, independent CONUS coordinate (near Washington, DC) cross-checks that the
        // grid is applied position-dependently (not a constant offset).
        const double lon = -77.0;
        const double lat = 38.9;

        var result = await _service!.TransformPointAsync(lon, lat, 4267, 4269);

        result.Should().NotBeNull();
        var transformed = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        transformed.X.Should().BeApproximately(-76.9996986, 5e-6);
        transformed.Y.Should().BeApproximately(38.9001112, 5e-6);
    }
}
