// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class MapServerSettingsTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public MapServerSettingsTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MapServerSettings_LoadsServicesAndSettings()
    {
        await _fixture.RunAsync(nameof(MapServerSettings_LoadsServicesAndSettings), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            await StubServicesListAsync(page, now, new[]
            {
                new { serviceName = "TestService1" },
                new { serviceName = "TestService2" }
            });

            await StubServiceSettingsAsync(page, now, "TestService1", CreateDefaultSettings());

            await page.GotoAsync(BuildMapServerSettingsUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("mapserver-service-select").WaitForAsync();
            await page.GetByTestId("mapserver-service-select").ClickAsync();
            await page.GetByText("TestService1").ClickAsync();

            await page.GetByTestId("toggle-featureserver").WaitForAsync();
            await page.GetByTestId("toggle-mapserver").WaitForAsync();
            await page.GetByTestId("toggle-ogcfeatures").WaitForAsync();
            await page.GetByTestId("toggle-odata").WaitForAsync();
        });
    }

    [Fact]
    public async Task MapServerSettings_ToggleProtocols_UpdatesCorrectly()
    {
        await _fixture.RunAsync(nameof(MapServerSettings_ToggleProtocols_UpdatesCorrectly), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;
            var serviceName = "TestService1";

            await StubServicesListAsync(page, now, new[] { new { serviceName } });
            await StubServiceSettingsAsync(page, now, serviceName, CreateDefaultSettings());

            var protocolsUpdated = false;
            await page.RouteAsync("**/api/v1/admin/services/*/protocols", async route =>
            {
                if (route.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    protocolsUpdated = true;
                    var updatedSettings = CreateDefaultSettings();
                    updatedSettings.enabledProtocols = new[] { "FeatureServer", "OgcFeatures" };

                    await FulfillJsonAsync(route, new
                    {
                        success = true,
                        message = "Protocols updated",
                        timestamp = now,
                        data = updatedSettings
                    });
                    return;
                }

                await route.FulfillAsync(new RouteFulfillOptions { Status = 405 });
            });

            await page.GotoAsync(BuildMapServerSettingsUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("mapserver-service-select").ClickAsync();
            await page.GetByText(serviceName).ClickAsync();

            // Disable MapServer and OData
            await page.GetByTestId("toggle-mapserver").ClickAsync();
            await page.GetByTestId("toggle-odata").ClickAsync();

            await page.GetByTestId("mapserver-save-protocols").ClickAsync();

            Assert.True(protocolsUpdated);
            await page.GetByText("Protocols updated.").WaitForAsync();
        });
    }

    [Fact]
    public async Task MapServerSettings_UpdateRenderingSettings_SavesCorrectly()
    {
        await _fixture.RunAsync(nameof(MapServerSettings_UpdateRenderingSettings_SavesCorrectly), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;
            var serviceName = "TestService1";

            await StubServicesListAsync(page, now, new[] { new { serviceName } });
            await StubServiceSettingsAsync(page, now, serviceName, CreateDefaultSettings());

            var renderingUpdated = false;
            await page.RouteAsync("**/api/v1/admin/services/*/mapserver", async route =>
            {
                if (route.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    renderingUpdated = true;
                    var updatedSettings = CreateDefaultSettings();
                    updatedSettings.mapServer.maxImageWidth = 2048;
                    updatedSettings.mapServer.maxImageHeight = 2048;

                    await FulfillJsonAsync(route, new
                    {
                        success = true,
                        message = "MapServer settings updated",
                        timestamp = now,
                        data = updatedSettings
                    });
                    return;
                }

                await route.FulfillAsync(new RouteFulfillOptions { Status = 405 });
            });

            await page.GotoAsync(BuildMapServerSettingsUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("mapserver-service-select").ClickAsync();
            await page.GetByText(serviceName).ClickAsync();

            // Update image dimensions
            await page.GetByLabel("Max image width").FillAsync("2048");
            await page.GetByLabel("Max image height").FillAsync("2048");

            await page.GetByTestId("mapserver-save-rendering").ClickAsync();

            Assert.True(renderingUpdated);
            await page.GetByText("MapServer settings updated.").WaitForAsync();
        });
    }

    [Fact]
    public async Task MapServerSettings_InvalidImageDimensions_ShowsValidation()
    {
        await _fixture.RunAsync(nameof(MapServerSettings_InvalidImageDimensions_ShowsValidation), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;
            var serviceName = "TestService1";

            await StubServicesListAsync(page, now, new[] { new { serviceName } });
            await StubServiceSettingsAsync(page, now, serviceName, CreateDefaultSettings());

            await page.GotoAsync(BuildMapServerSettingsUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("mapserver-service-select").ClickAsync();
            await page.GetByText(serviceName).ClickAsync();

            // Try to enter invalid dimensions (too large)
            await page.GetByLabel("Max image width").FillAsync("99999");
            await page.GetByLabel("Max image height").FillAsync("99999");

            // The MudNumericField should enforce max constraints
            var widthValue = await page.GetByLabel("Max image width").InputValueAsync();
            var heightValue = await page.GetByLabel("Max image height").InputValueAsync();

            // Values should be clamped to max allowed (16384)
            Assert.True(int.Parse(widthValue) <= 16384);
            Assert.True(int.Parse(heightValue) <= 16384);
        });
    }

    [Fact]
    public async Task MapServerSettings_ServiceLoadFailure_ShowsError()
    {
        await _fixture.RunAsync(nameof(MapServerSettings_ServiceLoadFailure_ShowsError), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            await page.RouteAsync("**/api/v1/admin/services", async route =>
            {
                await FulfillJsonAsync(route, new
                {
                    success = false,
                    message = "Failed to load services",
                    timestamp = DateTimeOffset.UtcNow,
                    data = (object?)null
                }, status: 500);
            });

            await page.GotoAsync(BuildMapServerSettingsUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByText("Failed to load services").WaitForAsync();
        });
    }

    [Fact]
    public async Task MapServerSettings_ProtocolUpdateFailure_ShowsError()
    {
        await _fixture.RunAsync(nameof(MapServerSettings_ProtocolUpdateFailure_ShowsError), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;
            var serviceName = "TestService1";

            await StubServicesListAsync(page, now, new[] { new { serviceName } });
            await StubServiceSettingsAsync(page, now, serviceName, CreateDefaultSettings());

            await page.RouteAsync("**/api/v1/admin/services/*/protocols", async route =>
            {
                if (route.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    await FulfillJsonAsync(route, new
                    {
                        success = false,
                        message = "Failed to update protocols",
                        timestamp = now,
                        data = (object?)null
                    }, status: 500);
                    return;
                }

                await route.FulfillAsync(new RouteFulfillOptions { Status = 405 });
            });

            await page.GotoAsync(BuildMapServerSettingsUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("mapserver-service-select").ClickAsync();
            await page.GetByText(serviceName).ClickAsync();

            await page.GetByTestId("toggle-mapserver").ClickAsync();
            await page.GetByTestId("mapserver-save-protocols").ClickAsync();

            await page.GetByText("Failed to update protocols").WaitForAsync();
        });
    }

    [Fact]
    public async Task MapServerSettings_ConcurrentSaveOperations_HandlesCorrectly()
    {
        await _fixture.RunAsync(nameof(MapServerSettings_ConcurrentSaveOperations_HandlesCorrectly), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;
            var serviceName = "TestService1";

            await StubServicesListAsync(page, now, new[] { new { serviceName } });
            await StubServiceSettingsAsync(page, now, serviceName, CreateDefaultSettings());

            var saveCount = 0;
            await page.RouteAsync("**/api/v1/admin/services/*/protocols", async route =>
            {
                if (route.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref saveCount);
                    // Add a delay to simulate slow network
                    await Task.Delay(1000);

                    await FulfillJsonAsync(route, new
                    {
                        success = true,
                        message = "Protocols updated",
                        timestamp = now,
                        data = CreateDefaultSettings()
                    });
                    return;
                }

                await route.FulfillAsync(new RouteFulfillOptions { Status = 405 });
            });

            await page.GotoAsync(BuildMapServerSettingsUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("mapserver-service-select").ClickAsync();
            await page.GetByText(serviceName).ClickAsync();

            // Try to trigger multiple saves by clicking rapidly
            await page.GetByTestId("toggle-mapserver").ClickAsync();

            var save1Task = page.GetByTestId("mapserver-save-protocols").ClickAsync();
            await Task.Delay(100); // Small delay to ensure first click registers
            var save2Task = page.GetByTestId("mapserver-save-protocols").ClickAsync();

            await Task.WhenAll(save1Task, save2Task);

            // Wait for completion
            await page.GetByText("Protocols updated.").WaitForAsync();

            // Should only have processed one save due to disabled state during saving
            Assert.Equal(1, saveCount);
        });
    }

    private static string BuildMapServerSettingsUrl(string baseUrl)
        => baseUrl.TrimEnd('/') + "/mapserver";

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

    private static async Task StubServicesListAsync(IPage page, DateTimeOffset now, object[] services)
    {
        await page.RouteAsync("**/api/v1/admin/services", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = services
            });
        });
    }

    private static async Task StubServiceSettingsAsync(IPage page, DateTimeOffset now, string serviceName, dynamic settings)
    {
        await page.RouteAsync($"**/api/v1/admin/services/{serviceName}/settings", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = settings
            });
        });
    }

    private static dynamic CreateDefaultSettings() => new
    {
        serviceName = "TestService1",
        enabledProtocols = new[] { "FeatureServer", "MapServer", "OgcFeatures", "OData" },
        mapServer = new
        {
            maxImageWidth = 1024,
            maxImageHeight = 1024,
            defaultImageWidth = 400,
            defaultImageHeight = 400,
            defaultDpi = 96,
            defaultFormat = "png",
            defaultTransparent = true,
            maxFeaturesPerLayer = 1000
        }
    };
}
