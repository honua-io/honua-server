// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using NBomber.Contracts;
using NBomber.CSharp;

namespace Honua.LoadTests.Scenarios;

/// <summary>
/// Load scenario: <c>POST /stac/search</c> with a varying bbox + datetime
/// payload. Nominal rate is 50 requests/second over <see cref="LoadScenarioSettings.GetDuration"/>
/// (default 60 seconds). Used by the nightly load-soak workflow; not executed
/// in PR CI (Tier=Slow excluded). Build-time discoverability is asserted by
/// <see cref="Honua.LoadTests.LoadScenarioDiscoveryTests"/>.
/// </summary>
public static class StacSearchLoadScenario
{
    public const string ScenarioName = "stac_search_load";
    public const int NominalRatePerSecond = 50;

    private static readonly string[] _datetimeWindows =
    {
        "2023-01-01T00:00:00Z/2023-06-30T23:59:59Z",
        "2024-01-01T00:00:00Z/2024-12-31T23:59:59Z",
        "2025-01-01T00:00:00Z/2025-12-31T23:59:59Z",
        "../2024-12-31T23:59:59Z",
        "2024-06-01T00:00:00Z/.."
    };

    /// <summary>
    /// Builds the NBomber scenario. The HTTP client and base URL are captured
    /// at construction time so the scenario can be registered with
    /// <see cref="NBomberRunner.RegisterScenarios"/>.
    /// </summary>
    public static ScenarioProps Build()
    {
        var baseUrl = LoadScenarioSettings.GetBaseUrl();
        var rate = LoadScenarioSettings.GetRate(NominalRatePerSecond);
        var duration = LoadScenarioSettings.GetDuration();
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));

        return Scenario.Create(ScenarioName, async context =>
            {
                var random = new Random(unchecked((int)context.InvocationNumber));
                var (minX, minY, maxX, maxY) = GenerateBoundingBox(random);
                var datetime = _datetimeWindows[random.Next(_datetimeWindows.Length)];

                var payload = string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"bbox\":[{0},{1},{2},{3}],\"datetime\":\"{4}\",\"limit\":25}}",
                    minX,
                    minY,
                    maxX,
                    maxY,
                    datetime);

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync($"{baseUrl}/stac/search", content);

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail(message: $"HTTP {(int)response.StatusCode}");
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate, TimeSpan.FromSeconds(1), duration))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) GenerateBoundingBox(Random random)
    {
        // Bounding boxes inside a representative seeded extent (San Francisco bay area).
        // Sized to stay well below the spatial query limits used by the seeded data.
        var centerX = -122.50 + random.NextDouble() * 0.20;
        var centerY = 37.70 + random.NextDouble() * 0.12;
        var halfWidth = 0.0025 + random.NextDouble() * 0.005;
        var halfHeight = 0.0025 + random.NextDouble() * 0.005;
        return (centerX - halfWidth, centerY - halfHeight, centerX + halfWidth, centerY + halfHeight);
    }
}
