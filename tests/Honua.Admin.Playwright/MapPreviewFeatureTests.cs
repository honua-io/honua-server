// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class MapPreviewFeatureTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public MapPreviewFeatureTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PreviewPage_FeaturePopupShowsAttributes()
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl
        });
        var page = await context.NewPageAsync();

        var connectionId = Guid.NewGuid();
        const int layerId = 55;
        var now = DateTimeOffset.UtcNow;

        var previewStyle = new
        {
            version = 8,
            name = "Preview",
            sources = new { },
            layers = Array.Empty<object>()
        };

        await page.RouteAsync("**/connections", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new[]
                {
                    new
                    {
                        connectionId,
                        name = "primary-db",
                        description = "Primary connection",
                        host = "db.internal",
                        port = 5432,
                        databaseName = "honua",
                        username = "admin",
                        sslRequired = true,
                        sslMode = "Require",
                        storageType = "managed",
                        isActive = true,
                        healthStatus = "Healthy",
                        lastHealthCheck = now.AddMinutes(-5),
                        createdAt = now.AddDays(-1),
                        createdBy = "tester"
                    }
                }
            });
        });

        await page.RouteAsync("**/connections/*/layers", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new[]
                {
                    new
                    {
                        layerId,
                        layerName = "Parcels",
                        schema = "public",
                        table = "parcels",
                        description = "Parcel boundaries",
                        geometryType = "Polygon",
                        srid = 4326,
                        primaryKey = "id",
                        fieldCount = 2,
                        enabled = true,
                        serviceName = "default"
                    }
                }
            });
        });

        await page.RouteAsync("**/metadata/layers/*/style", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new
                {
                    mapLibreStyle = previewStyle
                }
            });
        });

        await page.RouteAsync("**/tiles/*/tile.json", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                bounds = new[] { -157.0, 18.0, -156.0, 19.0 }
            });
        });

        await page.GotoAsync(BuildPreviewUrl(baseUrl));

        await page.GetByTestId("preview-connection-select").WaitForAsync();
        await page.GetByTestId("preview-layer-select").WaitForAsync();
        await page.GetByTestId("map-preview-canvas").WaitForAsync();

        var containerId = await page.GetByTestId("map-preview-canvas").GetAttributeAsync("id");
        Assert.False(string.IsNullOrWhiteSpace(containerId));

        await page.WaitForFunctionAsync("window.maplibreInterop && window.maplibreInterop.triggerFeature");

        var properties = new
        {
            id = 42,
            owner = "Sample Owner"
        };

        await WaitForConditionAsync(async () =>
        {
            await page.EvaluateAsync(
                "(args) => window.maplibreInterop.triggerFeature(args.containerId, args.properties)",
                new { containerId, properties });
            return await page.GetByTestId("map-feature-popup").IsVisibleAsync();
        }, TimeSpan.FromSeconds(10), "Feature popup did not appear.");

        await page.GetByText("Sample Owner").WaitForAsync();
    }

    private static string? GetBaseUrl()
        => Environment.GetEnvironmentVariable("HONUA_ADMIN_E2E_BASE_URL");

    private static string BuildPreviewUrl(string baseUrl)
        => baseUrl.TrimEnd('/') + "/preview";

    private static async Task FulfillJsonAsync(IRoute route, object payload, int status = 200)
    {
        var body = JsonSerializer.Serialize(payload, _jsonOptions);
        await route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = body
        });
    }

    private static async Task WaitForConditionAsync(Func<Task<bool>> predicate, TimeSpan timeout, string errorMessage)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(errorMessage);
    }
}
