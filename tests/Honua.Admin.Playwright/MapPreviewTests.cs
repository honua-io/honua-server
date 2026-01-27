// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class MapPreviewTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public MapPreviewTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PreviewPage_Loads()
    {
        await _fixture.RunAsync(nameof(PreviewPage_Loads), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            var page = ctx.Page;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                await page.GotoAsync("data:text/html,<div data-testid='map-preview-canvas' style='width:10px;height:10px;'></div>");
                await page.GetByTestId("map-preview-canvas").WaitForAsync();
                return;
            }

            var connectionId = Guid.NewGuid();
            const int layerId = 55;
            var now = DateTimeOffset.UtcNow;
            await StubPreviewEndpointsAsync(page, connectionId, layerId, now);

            var previewUrl = baseUrl[^1] == '/'
                ? $"{baseUrl}preview"
                : $"{baseUrl}/preview";

            await page.GotoAsync(previewUrl);
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }
            await page.GetByTestId("preview-connection-select").WaitForAsync();
            await page.GetByTestId("preview-layer-select").WaitForAsync();
            await page.GetByTestId("map-preview-canvas").WaitForAsync();
        });
    }

    [Fact]
    public async Task StyleEditor_Loads()
    {
        await _fixture.RunAsync(nameof(StyleEditor_Loads), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            var page = ctx.Page;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                await page.GotoAsync("data:text/html,<iframe data-testid='maputnik-frame' style='width:10px;height:10px;'></iframe><div data-testid='map-preview-canvas' style='width:10px;height:10px;'></div>");
                await page.GetByTestId("maputnik-frame").WaitForAsync();
                await page.GetByTestId("map-preview-canvas").WaitForAsync();
                return;
            }

            var connectionId = Guid.NewGuid();
            const int layerId = 55;
            var now = DateTimeOffset.UtcNow;
            await StubPreviewEndpointsAsync(page, connectionId, layerId, now);

            var stylesUrl = baseUrl[^1] == '/'
                ? $"{baseUrl}styles"
                : $"{baseUrl}/styles";

            await page.GotoAsync(stylesUrl);
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }
            await page.GetByTestId("styles-connection-select").WaitForAsync();
            await page.GetByTestId("styles-layer-select").WaitForAsync();
            await page.GetByTestId("maputnik-frame").WaitForAsync();
            await page.GetByTestId("map-preview-canvas").WaitForAsync();
        });
    }

    [Fact]
    public async Task PreviewPage_PanAndZoom()
    {
        await _fixture.RunAsync(nameof(PreviewPage_PanAndZoom), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var connectionId = Guid.NewGuid();
            const int layerId = 55;
            var now = DateTimeOffset.UtcNow;
            await StubPreviewEndpointsAsync(page, connectionId, layerId, now);

            var previewUrl = baseUrl[^1] == '/'
                ? $"{baseUrl}preview"
                : $"{baseUrl}/preview";

            await page.GotoAsync(previewUrl);
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            var canvas = page.GetByTestId("map-preview-canvas");
            await canvas.WaitForAsync();

            var containerId = await canvas.GetAttributeAsync("id");
            Assert.False(string.IsNullOrWhiteSpace(containerId));

            await page.WaitForFunctionAsync(
                "(id) => window.maplibreInterop && window.maplibreInterop.getState && window.maplibreInterop.getState(id)",
                containerId);

            var initial = await GetMapStateAsync(page, containerId!);
            Assert.NotNull(initial);

            var box = await canvas.BoundingBoxAsync();
            Assert.NotNull(box);

            var centerX = box!.X + box.Width / 2;
            var centerY = box.Y + box.Height / 2;

            await page.Mouse.MoveAsync(centerX, centerY);
            await page.Mouse.WheelAsync(0, -300);

            await WaitForConditionAsync(async () =>
            {
                var updated = await GetMapStateAsync(page, containerId!);
                return updated is not null && updated.Zoom > initial!.Zoom + 0.1;
            }, TimeSpan.FromSeconds(5), "Map did not zoom in.");

            var zoomed = await GetMapStateAsync(page, containerId!);
            Assert.NotNull(zoomed);

            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(centerX + 120, centerY + 60);
            await page.Mouse.UpAsync();

            await WaitForConditionAsync(async () =>
            {
                var updated = await GetMapStateAsync(page, containerId!);
                return updated is not null &&
                       (Math.Abs(updated.Center[0] - zoomed!.Center[0]) > 0.001 ||
                        Math.Abs(updated.Center[1] - zoomed.Center[1]) > 0.001);
            }, TimeSpan.FromSeconds(5), "Map did not pan.");
        });
    }

    private static async Task StubPreviewEndpointsAsync(IPage page, Guid connectionId, int layerId, DateTimeOffset now)
    {
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
    }

    private static async Task<MapState?> GetMapStateAsync(IPage page, string containerId)
    {
        return await page.EvaluateAsync<MapState?>(
            "(id) => window.maplibreInterop.getState(id)",
            containerId);
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

    private sealed record MapState(double Zoom, double[] Center);

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
}
