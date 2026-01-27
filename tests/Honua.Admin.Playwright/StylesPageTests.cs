// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class StylesPageTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public StylesPageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StylesPage_SaveAndResetFlow()
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
        const int layerId = 77;
        var now = DateTimeOffset.UtcNow;
        var updateCount = 0;

        var baseStyle = new
        {
            version = 8,
            name = "Honua Base",
            sources = new { },
            layers = Array.Empty<object>()
        };

        var updatedStyle = new
        {
            version = 8,
            name = "Honua Edited",
            sources = new { },
            layers = Array.Empty<object>()
        };

        var updatedStyleAgain = new
        {
            version = 8,
            name = "Honua Edited Again",
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
            if (route.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                updateCount += 1;
                await FulfillJsonAsync(route, new
                {
                    success = true,
                    message = "updated",
                    timestamp = now,
                    data = new
                    {
                        mapLibreStyle = updatedStyle
                    }
                });
                return;
            }

            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new
                {
                    mapLibreStyle = baseStyle
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

        await page.GotoAsync(BuildStylesUrl(baseUrl));

        await page.GetByTestId("styles-connection-select").WaitForAsync();
        await page.GetByTestId("styles-layer-select").WaitForAsync();
        await page.GetByTestId("maputnik-frame").WaitForAsync();
        await page.WaitForFunctionAsync("window.maputnikBridge && window.maputnikBridge.getStyle && window.maputnikBridge.getStyle() !== null");

        var saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Save" });
        var resetButton = page.GetByRole(AriaRole.Button, new() { Name = "Reset" });

        await WaitForConditionAsync(() => saveButton.IsDisabledAsync(), TimeSpan.FromSeconds(10), "Save button did not start disabled.");
        await WaitForConditionAsync(() => resetButton.IsDisabledAsync(), TimeSpan.FromSeconds(10), "Reset button did not start disabled.");

        await page.EvaluateAsync(
            "(args) => window.maputnikBridge.loadStyle(args.style, { styleId: args.styleId })",
            new { style = updatedStyle, styleId = $"honua-layer-{layerId}" });

        await WaitForConditionAsync(async () => !await saveButton.IsDisabledAsync(), TimeSpan.FromSeconds(10), "Save button did not enable after style change.");

        await saveButton.ClickAsync();

        await WaitForConditionAsync(async () =>
            updateCount > 0 && await saveButton.IsDisabledAsync(),
            TimeSpan.FromSeconds(10),
            "Save button did not disable after saving.");

        await page.EvaluateAsync(
            "(args) => window.maputnikBridge.loadStyle(args.style, { styleId: args.styleId })",
            new { style = updatedStyleAgain, styleId = $"honua-layer-{layerId}" });

        await WaitForConditionAsync(async () => !await resetButton.IsDisabledAsync(), TimeSpan.FromSeconds(10), "Reset button did not enable after style change.");

        await resetButton.ClickAsync();

        await WaitForConditionAsync(() => resetButton.IsDisabledAsync(), TimeSpan.FromSeconds(10), "Reset button did not disable after reset.");
    }

    private static string? GetBaseUrl()
        => Environment.GetEnvironmentVariable("HONUA_ADMIN_E2E_BASE_URL");

    private static string BuildStylesUrl(string baseUrl)
        => baseUrl.TrimEnd('/') + "/styles";

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
