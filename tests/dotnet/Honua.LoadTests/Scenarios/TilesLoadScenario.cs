// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using NBomber.Contracts;
using NBomber.CSharp;

namespace Honua.LoadTests.Scenarios;

/// <summary>
/// Load scenario: <c>GET /ogc/tiles/collections/{collection}/styles/default/tiles/{tileMatrixSet}/{z}/{y}/{x}</c>
/// with z/x/y chosen from a representative set of low-zoom tiles. Nominal rate
/// is 100 requests/second over <see cref="LoadScenarioSettings.GetDuration"/>
/// (default 60 seconds). Driven by the nightly load-soak workflow and
/// excluded from PR CI via Tier=Slow.
/// </summary>
public static class TilesLoadScenario
{
    public const string ScenarioName = "tiles_styles_load";
    public const int NominalRatePerSecond = 100;
    public const string DefaultCollectionId = "0";
    public const string DefaultTileMatrixSet = "WebMercatorQuad";

    /// <summary>
    /// Representative low-zoom tile coordinates exercising index lookups and
    /// MVT encoding across a handful of cells.
    /// </summary>
    internal static readonly (int Z, int Y, int X)[] TileCoordinates =
    {
        (0, 0, 0),
        (1, 0, 0),
        (1, 0, 1),
        (1, 1, 0),
        (1, 1, 1),
        (2, 1, 1),
        (2, 1, 2),
        (2, 2, 1),
        (2, 2, 2),
        (3, 2, 2),
        (3, 3, 3)
    };

    public static ScenarioProps Build()
    {
        var baseUrl = LoadScenarioSettings.GetBaseUrl();
        var rate = LoadScenarioSettings.GetRate(NominalRatePerSecond);
        var duration = LoadScenarioSettings.GetDuration();
        var collectionId = Environment.GetEnvironmentVariable("HONUA_LOAD_COLLECTION_ID");
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            collectionId = DefaultCollectionId;
        }

        var tileMatrixSet = Environment.GetEnvironmentVariable("HONUA_LOAD_TILE_MATRIX_SET");
        if (string.IsNullOrWhiteSpace(tileMatrixSet))
        {
            tileMatrixSet = DefaultTileMatrixSet;
        }

        var httpClient = new HttpClient();

        return Scenario.Create(ScenarioName, async context =>
            {
                var tile = TileCoordinates[(int)(context.InvocationNumber % TileCoordinates.Length)];
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/ogc/tiles/collections/{1}/styles/default/tiles/{2}/{3}/{4}/{5}",
                    baseUrl,
                    collectionId,
                    tileMatrixSet,
                    tile.Z,
                    tile.Y,
                    tile.X);

                using var response = await httpClient.GetAsync(url);

                // 204 No Content is a legitimate "empty tile" response from OGC Tiles
                // and should not be counted as a failure under load.
                return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent
                    ? Response.Ok()
                    : Response.Fail(message: $"HTTP {(int)response.StatusCode}");
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate, TimeSpan.FromSeconds(1), duration))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }
}
