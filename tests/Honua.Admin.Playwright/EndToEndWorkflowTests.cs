// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class EndToEndWorkflowTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public EndToEndWorkflowTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CompleteWorkflow_CreateConnection_ImportData_PublishLayer_PreviewMap()
    {
        await _fixture.RunAsync(nameof(CompleteWorkflow_CreateConnection_ImportData_PublishLayer_PreviewMap), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            var connectionId = Guid.NewGuid();
            var jobId = Guid.NewGuid().ToString();
            var layerId = 1;

            var connectionCreated = false;
            var importStarted = false;
            var layerPublished = false;

            // Step 1: Setup API mocks for connections
            await page.RouteAsync("**/api/v1/admin/connections", async route =>
            {
                if (route.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    connectionCreated = true;
                    await FulfillJsonAsync(route, new
                    {
                        success = true,
                        message = "Connection created",
                        timestamp = now,
                        data = new
                        {
                            connectionId,
                            name = "e2e-test-connection",
                            description = "End-to-end test connection",
                            host = "testdb.example.com",
                            port = 5432,
                            databaseName = "geodata",
                            username = "testuser",
                            sslRequired = true,
                            sslMode = "Require",
                            storageType = "managed",
                            isActive = true,
                            healthStatus = "Unknown",
                            lastHealthCheck = (DateTimeOffset?)null,
                            createdAt = now,
                            createdBy = "e2e-tester"
                        }
                    });
                    return;
                }

                if (connectionCreated)
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
                                name = "e2e-test-connection",
                                description = "End-to-end test connection",
                                host = "testdb.example.com",
                                port = 5432,
                                databaseName = "geodata",
                                username = "testuser",
                                sslRequired = true,
                                sslMode = "Require",
                                storageType = "managed",
                                isActive = true,
                                healthStatus = "Healthy",
                                lastHealthCheck = now,
                                createdAt = now,
                                createdBy = "e2e-tester"
                            }
                        }
                    });
                }
                else
                {
                    await FulfillJsonAsync(route, new
                    {
                        success = true,
                        message = "ok",
                        timestamp = now,
                        data = Array.Empty<object>()
                    });
                }
            });

            // Setup import API mocks
            await page.RouteAsync("**/import/geoservices/jobs", async route =>
            {
                if (route.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await FulfillJsonAsync(route, Array.Empty<object>());
                }
            });

            await page.RouteAsync("**/import/geoservices/discover", async route =>
            {
                await FulfillJsonAsync(route, new
                {
                    serviceUrl = "https://example.com/arcgis/rest/services/TestService/MapServer",
                    serviceName = "TestService",
                    layers = new[]
                    {
                        new
                        {
                            id = layerId,
                            name = "Test Points",
                            type = "Feature Layer",
                            geometryType = "esriGeometryPoint",
                            featureCount = 150,
                            hasZ = false
                        }
                    }
                });
            });

            await page.RouteAsync("**/import/geoservices/start", async route =>
            {
                importStarted = true;
                await FulfillJsonAsync(route, new
                {
                    jobId,
                    message = "Import job started"
                });
            });

            await page.RouteAsync($"**/import/geoservices/jobs/{jobId}", async route =>
            {
                await FulfillJsonAsync(route, new
                {
                    jobId,
                    status = "Completed",
                    progress = 100,
                    message = "Import completed successfully",
                    startedAt = now,
                    completedAt = now.AddMinutes(2)
                });
            });

            // Setup layer publishing mocks
            await page.RouteAsync("**/api/v1/admin/layers/publish", async route =>
            {
                if (route.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    layerPublished = true;
                    await FulfillJsonAsync(route, new
                    {
                        success = true,
                        message = "Layer published",
                        timestamp = now,
                        data = new
                        {
                            layerId = $"test_points_{layerId}",
                            layerName = "Test Points",
                            connectionId,
                            tableName = "test_points",
                            isPublished = true,
                            geometryType = "Point",
                            featureCount = 150
                        }
                    });
                }
            });

            await page.RouteAsync($"**/api/v1/admin/connections/{connectionId}/layers", async route =>
            {
                if (layerPublished)
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
                                layerId = $"test_points_{layerId}",
                                layerName = "Test Points",
                                tableName = "test_points",
                                isPublished = true,
                                geometryType = "Point",
                                featureCount = 150,
                                createdAt = now
                            }
                        }
                    });
                }
                else
                {
                    await FulfillJsonAsync(route, new
                    {
                        success = true,
                        message = "ok",
                        timestamp = now,
                        data = Array.Empty<object>()
                    });
                }
            });

            // Step 1: Create a connection
            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/connections");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("connections-add").ClickAsync();
            await page.GetByLabel("Connection name").FillAsync("e2e-test-connection");
            await page.GetByLabel("Description").FillAsync("End-to-end test connection");
            await page.GetByLabel("Host").FillAsync("testdb.example.com");
            await page.GetByLabel("Database").FillAsync("geodata");
            await page.GetByLabel("Username").FillAsync("testuser");
            await page.GetByLabel("Password").FillAsync("testpass");
            await page.GetByTestId("connection-save").ClickAsync();

            await WaitForTextAsync(page, "e2e-test-connection");
            Assert.True(connectionCreated, "Connection should have been created");

            // Step 2: Navigate to import and discover data
            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/import");
            await page.GetByTestId("import-service-url").Locator("input")
                .FillAsync("https://example.com/arcgis/rest/services/TestService/MapServer");
            await page.GetByTestId("import-discover").ClickAsync();

            await page.GetByText("Test Points").WaitForAsync();

            // Step 3: Configure and start import
            await page.GetByTestId($"import-layer-select-{layerId}").ClickAsync();
            await page.GetByTestId($"import-layer-table-{layerId}").Locator("input").FillAsync("test_points");
            await page.GetByTestId($"import-layer-connection-{layerId}").ClickAsync();
            await page.GetByText("e2e-test-connection").ClickAsync();

            await page.GetByTestId("import-start").ClickAsync();
            Assert.True(importStarted, "Import should have been started");

            // Wait for import completion notification
            await WaitForTextAsync(page, "Import job started");

            // Step 4: Navigate to layers and publish the imported data
            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/layers");
            await page.GetByTestId("layer-connection-select").ClickAsync();
            await page.GetByText("e2e-test-connection").ClickAsync();

            // Wait for layers to load, then publish
            await page.GetByText("test_points").WaitForAsync();
            await page.GetByTestId("layer-publish-test_points").ClickAsync();
            await page.GetByTestId("publish-layer-name").FillAsync("Test Points");
            await page.GetByTestId("publish-save").ClickAsync();

            Assert.True(layerPublished, "Layer should have been published");
            await WaitForTextAsync(page, "Layer published");

            // Step 5: Navigate to preview and verify the layer appears
            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/preview");
            await page.GetByTestId("preview-service-select").WaitForAsync();

            // The layer should be available for preview
            await WaitForTextAsync(page, "Test Points");

            // Verify the complete workflow was successful
            Assert.True(connectionCreated, "Connection creation failed");
            Assert.True(importStarted, "Import start failed");
            Assert.True(layerPublished, "Layer publishing failed");
        });
    }

    [Fact]
    public async Task Workflow_ConnectionFailure_StopsWorkflow()
    {
        await _fixture.RunAsync(nameof(Workflow_ConnectionFailure_StopsWorkflow), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            // Mock connection test failure
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

            await page.RouteAsync("**/api/v1/admin/connections/test", async route =>
            {
                await FulfillJsonAsync(route, new
                {
                    success = false,
                    message = "Connection test failed",
                    timestamp = now,
                    data = new
                    {
                        isHealthy = false,
                        testedAt = now,
                        message = "Unable to connect to database: Connection refused"
                    }
                }, status: 400);
            });

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/connections");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("connections-add").ClickAsync();
            await page.GetByLabel("Connection name").FillAsync("failing-connection");
            await page.GetByLabel("Host").FillAsync("invalid-host.example.com");
            await page.GetByLabel("Database").FillAsync("geodata");
            await page.GetByLabel("Username").FillAsync("testuser");
            await page.GetByLabel("Password").FillAsync("testpass");

            // Test connection before saving
            await page.GetByTestId("connection-test-draft").ClickAsync();

            await WaitForTextAsync(page, "Connection refused");

            // User should be warned about the connection failure
            await page.GetByText("Unable to connect to database").WaitForAsync();

            // Save button should still be enabled (user might want to save anyway)
            Assert.False(await page.GetByTestId("connection-save").IsDisabledAsync());
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

    private static async Task WaitForTextAsync(IPage page, string expected, int timeoutMs = 10_000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.GetByText(expected).IsVisibleAsync())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for text '{expected}'.");
    }
}
