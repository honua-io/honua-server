// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class LayersPageTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public LayersPageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LayersPage_PublishLayerFlow()
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
        const int layerId = 321;
        var now = DateTimeOffset.UtcNow;
        var publishedLayers = new List<object>();

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

        await page.RouteAsync("**/connections/*/tables", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                tables = new[]
                {
                    new
                    {
                        schema = "public",
                        table = "parcels",
                        geometryColumn = "geom",
                        geometryType = "Polygon",
                        srid = 4326,
                        estimatedRows = 120,
                        columns = new[]
                        {
                            new
                            {
                                name = "id",
                                dataType = "integer",
                                isNullable = false,
                                isPrimaryKey = true,
                                maxLength = (int?)null
                            },
                            new
                            {
                                name = "owner",
                                dataType = "text",
                                isNullable = true,
                                isPrimaryKey = false,
                                maxLength = (int?)255
                            }
                        }
                    }
                }
            });
        });

        await page.RouteAsync("**/connections/*/layers", async route =>
        {
            if (route.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var published = new
                {
                    layerId,
                    layerName = "parcels",
                    schema = "public",
                    table = "parcels",
                    description = "Parcel boundaries",
                    geometryType = "Polygon",
                    srid = 4326,
                    primaryKey = "id",
                    fieldCount = 2,
                    enabled = true,
                    serviceName = "default"
                };

                publishedLayers.Clear();
                publishedLayers.Add(published);

                await FulfillJsonAsync(route, new
                {
                    success = true,
                    message = "ok",
                    timestamp = now,
                    data = published
                });
                return;
            }

            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = publishedLayers
            });
        });

        await page.GotoAsync(BuildLayersUrl(baseUrl));

        await page.GetByText("public.parcels").WaitForAsync();
        await page.GetByTestId("table-publish-public-parcels").ClickAsync();
        await page.GetByText("Publish layer").WaitForAsync();
        await page.GetByTestId("layer-publish-submit").ClickAsync();

        await page.GetByTestId($"layer-toggle-{layerId}").WaitForAsync();
    }

    [Fact]
    public async Task LayersPage_ToggleLayerUpdatesState()
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
        const int layerId = 42;
        var now = DateTimeOffset.UtcNow;

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

        await page.RouteAsync("**/connections/*/tables", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                tables = new[]
                {
                    new
                    {
                        schema = "public",
                        table = "parcels",
                        geometryColumn = "geom",
                        geometryType = "Polygon",
                        srid = 4326,
                        estimatedRows = 120,
                        columns = new[]
                        {
                            new
                            {
                                name = "id",
                                dataType = "integer",
                                isNullable = false,
                                isPrimaryKey = true,
                                maxLength = (int?)null
                            }
                        }
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
                        fieldCount = 1,
                        enabled = true,
                        serviceName = "default"
                    }
                }
            });
        });

        await page.RouteAsync("**/connections/*/layers/*/enabled", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new
                {
                    layerId,
                    layerName = "Parcels",
                    schema = "public",
                    table = "parcels",
                    description = "Parcel boundaries",
                    geometryType = "Polygon",
                    srid = 4326,
                    primaryKey = "id",
                    fieldCount = 1,
                    enabled = false,
                    serviceName = "default"
                }
            });
        });

        await page.GotoAsync(BuildLayersUrl(baseUrl));

        var toggle = page.GetByTestId($"layer-toggle-{layerId}");
        await toggle.WaitForAsync();

        var input = toggle.Locator("input");
        await WaitForConditionAsync(() => input.IsCheckedAsync(), TimeSpan.FromSeconds(5), "Layer toggle did not start checked.");

        await toggle.ClickAsync();

        await WaitForConditionAsync(async () => !await input.IsCheckedAsync(), TimeSpan.FromSeconds(5), "Layer toggle did not update to unchecked.");
    }

    private static string? GetBaseUrl()
        => Environment.GetEnvironmentVariable("HONUA_ADMIN_E2E_BASE_URL");

    private static string BuildLayersUrl(string baseUrl)
        => baseUrl.TrimEnd('/') + "/layers";

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
