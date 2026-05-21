// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NBomber.Contracts;
using NBomber.CSharp;

namespace Honua.LoadTests.Scenarios;

/// <summary>
/// Load scenario: <c>GET /ogc/features/collections/{c}/items?limit=100&amp;offset=N</c>
/// walking offsets 0..1000 (in increments of 100). Nominal rate is 30
/// requests/second over <see cref="LoadScenarioSettings.GetDuration"/>
/// (default 60 seconds). Exercises the OGC Features pagination cursor and
/// downstream Postgres LIMIT/OFFSET planning under sustained traffic.
/// </summary>
public static class FeaturesPaginationLoadScenario
{
    public const string ScenarioName = "features_pagination_load";
    public const int NominalRatePerSecond = 30;
    public const int Limit = 100;
    public const int MaxOffset = 1000;
    public const string DefaultCollectionId = "0";

    internal static readonly int[] OffsetWalk = BuildOffsetWalk();

    private static int[] BuildOffsetWalk()
    {
        var walk = new int[(MaxOffset / Limit) + 1];
        for (var i = 0; i <= MaxOffset / Limit; i++)
        {
            walk[i] = i * Limit;
        }

        return walk;
    }

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

        var httpClient = new HttpClient();

        return Scenario.Create(ScenarioName, async context =>
            {
                var offset = OffsetWalk[(int)(context.InvocationNumber % OffsetWalk.Length)];
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/ogc/features/collections/{1}/items?limit={2}&offset={3}",
                    baseUrl,
                    collectionId,
                    Limit,
                    offset);

                using var response = await httpClient.GetAsync(url);

                return response.IsSuccessStatusCode
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
