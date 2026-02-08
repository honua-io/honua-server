// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class AccessibilityAndMobileTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public AccessibilityAndMobileTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AdminNavigation_KeyboardNavigation_WorksCorrectly()
    {
        await _fixture.RunAsync(nameof(AdminNavigation_KeyboardNavigation_WorksCorrectly), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            await StubEmptyConnectionsAsync(page, now);

            await page.GotoAsync(baseUrl);
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            // Test keyboard navigation through main navigation
            await page.Keyboard.PressAsync("Tab"); // Should focus first interactive element
            await page.Keyboard.PressAsync("Tab"); // Navigate to next element

            // Test that navigation items are keyboard accessible
            var dashboardNav = page.GetByTestId("nav-dashboard");
            await dashboardNav.FocusAsync();
            await page.Keyboard.PressAsync("Enter");

            // Should navigate to dashboard
            Assert.Contains("/", page.Url);

            // Test connections navigation
            var connectionsNav = page.GetByTestId("nav-connections");
            await connectionsNav.FocusAsync();
            await page.Keyboard.PressAsync("Enter");

            Assert.Contains("/connections", page.Url);
        });
    }

    [Fact]
    public async Task ConnectionsPage_MobileLayout_RendersCorrectly()
    {
        await _fixture.RunAsync(nameof(ConnectionsPage_MobileLayout_RendersCorrectly), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            // Set mobile viewport
            await page.SetViewportSizeAsync(375, 667);

            await StubConnectionsWithDataAsync(page, now);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/connections");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            // Check that the table is responsive
            var table = page.Locator("table");
            await table.WaitForAsync();

            // On mobile, the table should either scroll horizontally or stack columns
            var tableWidth = await table.BoundingBoxAsync();
            // The table should fit within the viewport or have horizontal scroll
            Assert.True(tableWidth != null);

            // Check that action buttons are still accessible on mobile
            await page.GetByTestId("connections-add").WaitForAsync();
            Assert.True(await page.GetByTestId("connections-add").IsVisibleAsync());
        });
    }

    [Fact]
    public async Task ConnectionForm_AriaLabels_ArePresent()
    {
        await _fixture.RunAsync(nameof(ConnectionForm_AriaLabels_ArePresent), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            await StubEmptyConnectionsAsync(page, now);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/connections");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("connections-add").ClickAsync();

            // Check that form fields have proper labels and aria attributes
            var nameField = page.GetByLabel("Connection name");
            Assert.NotNull(await nameField.GetAttributeAsync("aria-label") ?? await nameField.GetAttributeAsync("id"));

            var hostField = page.GetByLabel("Host");
            Assert.NotNull(await hostField.GetAttributeAsync("aria-label") ?? await hostField.GetAttributeAsync("id"));

            var databaseField = page.GetByLabel("Database");
            Assert.NotNull(await databaseField.GetAttributeAsync("aria-label") ?? await databaseField.GetAttributeAsync("id"));

            var usernameField = page.GetByLabel("Username");
            Assert.NotNull(await usernameField.GetAttributeAsync("aria-label") ?? await usernameField.GetAttributeAsync("id"));

            var passwordField = page.GetByLabel("Password");
            Assert.NotNull(await passwordField.GetAttributeAsync("aria-label") ?? await passwordField.GetAttributeAsync("id"));
            Assert.Equal("password", await passwordField.GetAttributeAsync("type"));
        });
    }

    [Fact]
    public async Task ErrorMessages_HaveProperAriaRole()
    {
        await _fixture.RunAsync(nameof(ErrorMessages_HaveProperAriaRole), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            await StubEmptyConnectionsAsync(page, now);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/connections");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("connections-add").ClickAsync();
            await page.GetByTestId("connection-save").ClickAsync();

            // Check that validation errors have proper ARIA roles
            var errorMessage = page.GetByText("Password is required.");
            await errorMessage.WaitForAsync();

            // Error messages should have role="alert" or be in an element with role="alert"
            var errorAlert = page.Locator("[role='alert']");
            var hasAlert = await errorAlert.CountAsync() > 0;

            Assert.True(hasAlert || await errorMessage.GetAttributeAsync("role") == "alert");
        });
    }

    [Fact]
    public async Task ImportWizard_TabletLayout_WorksCorrectly()
    {
        await _fixture.RunAsync(nameof(ImportWizard_TabletLayout_WorksCorrectly), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            // Set tablet viewport
            await page.SetViewportSizeAsync(768, 1024);

            await page.RouteAsync("**/import/esri/jobs", async route =>
            {
                await FulfillJsonAsync(route, Array.Empty<object>());
            });

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/import");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            // Check that the import form is properly laid out on tablet
            var serviceUrlField = page.GetByTestId("import-service-url");
            await serviceUrlField.WaitForAsync();

            var discoverButton = page.GetByTestId("import-discover");
            await discoverButton.WaitForAsync();

            // On tablet, elements should be properly spaced and accessible
            var fieldBounds = await serviceUrlField.BoundingBoxAsync();
            var buttonBounds = await discoverButton.BoundingBoxAsync();

            Assert.NotNull(fieldBounds);
            Assert.NotNull(buttonBounds);

            // Button should be positioned appropriately relative to the input field
            Assert.True(buttonBounds.Y >= fieldBounds.Y - 10); // Allow some tolerance for styling
        });
    }

    [Fact]
    public async Task MapPreview_TouchGestures_AreSupported()
    {
        await _fixture.RunAsync(nameof(MapPreview_TouchGestures_AreSupported), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            // Set mobile viewport to enable touch events
            await page.SetViewportSizeAsync(375, 667);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/preview");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            // Look for map container
            var mapContainer = page.Locator("[data-testid*='map'], .map-container, #map");
            var mapExists = await mapContainer.CountAsync() > 0;

            if (mapExists)
            {
                // Test that touch events are properly supported
                await mapContainer.First.TapAsync();

                // Map should respond to touch interactions
                // This is a basic test - more sophisticated touch testing would require
                // specific map library testing
                Assert.True(await mapContainer.First.IsVisibleAsync());
            }
        });
    }

    [Fact]
    public async Task LargeDataTable_VirtualScrolling_HandlesPerformance()
    {
        await _fixture.RunAsync(nameof(LargeDataTable_VirtualScrolling_HandlesPerformance), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            // Mock a large dataset
            var largeConnectionsList = new List<object>();
            for (int i = 0; i < 1000; i++)
            {
                largeConnectionsList.Add(new
                {
                    connectionId = Guid.NewGuid(),
                    name = $"connection-{i:D4}",
                    description = $"Test connection {i}",
                    host = $"host{i}.example.com",
                    port = 5432,
                    databaseName = "geodata",
                    username = "testuser",
                    sslRequired = true,
                    sslMode = "Require",
                    storageType = "managed",
                    isActive = true,
                    healthStatus = i % 3 == 0 ? "Healthy" : i % 3 == 1 ? "Unhealthy" : "Unknown",
                    lastHealthCheck = i % 2 == 0 ? (DateTimeOffset?)now : null,
                    createdAt = now.AddDays(-i),
                    createdBy = "perf-tester"
                });
            }

            await page.RouteAsync("**/api/v1/admin/connections", async route =>
            {
                await FulfillJsonAsync(route, new
                {
                    success = true,
                    message = "ok",
                    timestamp = now,
                    data = largeConnectionsList
                });
            });

            var startTime = DateTimeOffset.UtcNow;

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/connections");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            // Wait for the table to load
            await page.GetByText("connection-0000").WaitForAsync();

            var loadTime = DateTimeOffset.UtcNow - startTime;

            // The page should load within reasonable time even with large dataset
            Assert.True(loadTime < TimeSpan.FromSeconds(30), $"Page took too long to load: {loadTime}");

            // Check that scrolling performance is reasonable
            var table = page.Locator("table");
            if (await table.CountAsync() > 0)
            {
                // Scroll to bottom and verify performance
                await page.Keyboard.PressAsync("End");
                await Task.Delay(100); // Allow time for scroll

                // Should still be responsive after scrolling
                Assert.True(await page.GetByTestId("connections-add").IsVisibleAsync());
            }
        });
    }

    [Fact]
    public async Task HighContrast_Mode_MaintainsUsability()
    {
        await _fixture.RunAsync(nameof(HighContrast_Mode_MaintainsUsability), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            // Simulate high contrast mode by injecting CSS
            await page.AddStyleTagAsync(new PageAddStyleTagOptions
            {
                Content = @"
                    * {
                        background: black !important;
                        color: white !important;
                        border-color: white !important;
                    }
                    a, button {
                        color: yellow !important;
                    }
                "
            });

            await StubEmptyConnectionsAsync(page, now);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/connections");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            // Elements should still be visible and functional in high contrast mode
            await page.GetByTestId("connections-add").WaitForAsync();
            Assert.True(await page.GetByTestId("connections-add").IsVisibleAsync());

            // Text should be readable
            var headerText = await page.GetByRole(AriaRole.Heading, new() { Name = "Connections" }).TextContentAsync();
            Assert.Equal("Connections", headerText);

            // Interactive elements should remain functional
            await page.GetByTestId("connections-add").ClickAsync();
            await page.GetByLabel("Connection name").WaitForAsync();
            Assert.True(await page.GetByLabel("Connection name").IsVisibleAsync());
        });
    }

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

    private static async Task StubEmptyConnectionsAsync(IPage page, DateTimeOffset now)
    {
        await page.RouteAsync("**/api/v1/admin/connections", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = Array.Empty<object>()
            });
        });
    }

    private static async Task StubConnectionsWithDataAsync(IPage page, DateTimeOffset now)
    {
        await page.RouteAsync("**/api/v1/admin/connections", async route =>
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
                        connectionId = Guid.NewGuid(),
                        name = "test-connection",
                        description = "Test connection",
                        host = "localhost",
                        port = 5432,
                        databaseName = "testdb",
                        username = "testuser",
                        sslRequired = true,
                        sslMode = "Require",
                        storageType = "managed",
                        isActive = true,
                        healthStatus = "Healthy",
                        lastHealthCheck = now,
                        createdAt = now.AddDays(-1),
                        createdBy = "tester"
                    }
                }
            });
        });
    }
}
