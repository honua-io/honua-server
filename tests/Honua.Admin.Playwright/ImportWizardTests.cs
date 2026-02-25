// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class ImportWizardTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public ImportWizardTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ImportWizard_DiscoverAndValidateSelection()
    {
        await _fixture.RunAsync(nameof(ImportWizard_DiscoverAndValidateSelection), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            await StubListJobsAsync(page, Array.Empty<object>());
            await StubDiscoverAsync(page, new[]
            {
                new LayerStub(10, "Small Points", "esriGeometryPoint", 48, false),
                new LayerStub(12, "Short Lines", "esriGeometryPolyline", 92, true)
            });

            await page.GotoAsync(BuildImportUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("import-service-url").Locator("input")
                .FillAsync("https://geodata.hawaii.gov/arcgis/rest/services/Infrastructure/MapServer");
            await page.GetByTestId("import-discover").ClickAsync();

            await page.GetByTestId("import-layers-table").GetByText("Small Points").WaitForAsync();

            await page.GetByTestId("import-layer-select-10").ClickAsync();
            await page.GetByTestId("import-layer-select-12").ClickAsync();

            await page.GetByTestId("import-layer-table-10").Locator("input").FillAsync("dup_table");
            await page.GetByTestId("import-layer-table-12").Locator("input").FillAsync("dup_table");

            await page.GetByText("Table name 'dup_table' is used by multiple selected layers.").WaitForAsync();
            await WaitForConditionAsync(
                () => page.GetByTestId("import-start").IsDisabledAsync(),
                TimeSpan.FromSeconds(5),
                "Start import button did not disable for duplicate table names.");

            await page.GetByTestId("import-layer-table-12").Locator("input").FillAsync("unique_table");

            await WaitForConditionAsync(
                async () => !await page.GetByTestId("import-start").IsDisabledAsync(),
                TimeSpan.FromSeconds(5),
                "Start import button did not enable after fixing table names.");
        });
    }

    [Fact]
    public async Task ImportWizard_InvalidUrlShowsError()
    {
        await _fixture.RunAsync(nameof(ImportWizard_InvalidUrlShowsError), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            await StubListJobsAsync(page, Array.Empty<object>());

            await page.GotoAsync(BuildImportUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("import-service-url").Locator("input").FillAsync("not-a-url");
            await page.GetByTestId("import-discover").ClickAsync();

            await page.GetByText("Enter a valid HTTPS service URL.").WaitForAsync();
        });
    }

    [Fact]
    public async Task ImportWizard_StartImportCompletes()
    {
        await _fixture.RunAsync(nameof(ImportWizard_StartImportCompletes), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            await StubListJobsAsync(page, Array.Empty<object>());
            await StubDiscoverAsync(page, new[]
            {
                new LayerStub(3, "Moku", "esriGeometryPolygon", 12, false)
            });

            var jobCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            await page.RouteAsync("**/api/v1/admin/import/geoservices/start", async route =>
            {
                const string jobId = "job-success";
                jobCalls[jobId] = 0;

                await FulfillJsonAsync(route, new
                {
                    jobId,
                    message = "Import queued",
                    statusUrl = $"/api/v1/admin/import/geoservices/jobs/{jobId}",
                    cancelUrl = $"/api/v1/admin/import/geoservices/jobs/{jobId}/cancel"
                }, status: 202);
            });

            await page.RouteAsync("**/api/v1/admin/import/geoservices/jobs/*", async route =>
            {
                var jobId = ExtractJobId(route.Request.Url);
                if (!jobCalls.ContainsKey(jobId))
                {
                    jobCalls[jobId] = 0;
                }

                jobCalls[jobId] += 1;
                var status = jobCalls[jobId] < 2 ? 4 : 6; // InsertingFeatures -> Completed

                await FulfillJsonAsync(route, BuildJobPayload(jobId, status, 3, "moku"));
            });

            await page.GotoAsync(BuildImportUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("import-service-url").Locator("input")
                .FillAsync("https://geodata.hawaii.gov/arcgis/rest/services/HistoricCultural/MapServer");
            await page.GetByTestId("import-discover").ClickAsync();
            await page.GetByTestId("import-layer-select-3").ClickAsync();

            await page.GetByTestId("import-start").ClickAsync();

            await page.GetByTestId("import-job-status-job-success").WaitForAsync();
            await WaitForTextAsync(page.GetByTestId("import-job-status-job-success"), "Completed");
        });
    }

    [Fact]
    public async Task ImportWizard_FailedJobCanRetry()
    {
        await _fixture.RunAsync(nameof(ImportWizard_FailedJobCanRetry), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            await StubListJobsAsync(page, Array.Empty<object>());
            await StubDiscoverAsync(page, new[]
            {
                new LayerStub(2, "Bridges", "esriGeometryPoint", 5, false)
            });

            var startCount = 0;

            await page.RouteAsync("**/api/v1/admin/import/geoservices/start", async route =>
            {
                startCount += 1;
                var jobId = startCount == 1 ? "job-failed" : "job-retry";

                await FulfillJsonAsync(route, new
                {
                    jobId,
                    message = "Import queued",
                    statusUrl = $"/api/v1/admin/import/geoservices/jobs/{jobId}",
                    cancelUrl = $"/api/v1/admin/import/geoservices/jobs/{jobId}/cancel"
                }, status: 202);
            });

            await page.RouteAsync("**/api/v1/admin/import/geoservices/jobs/*", async route =>
            {
                var jobId = ExtractJobId(route.Request.Url);
                var status = jobId == "job-failed" ? 7 : 6;
                var errorMessage = jobId == "job-failed" ? "Service returned 500" : null;

                await FulfillJsonAsync(route, BuildJobPayload(jobId, status, 2, "bridges", errorMessage));
            });

            await page.GotoAsync(BuildImportUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("import-service-url").Locator("input")
                .FillAsync("https://maps.kauai.gov/server/rest/services/Bridges_with_condition_where_available/FeatureServer");
            await page.GetByTestId("import-discover").ClickAsync();
            await page.GetByTestId("import-layer-select-2").ClickAsync();
            await page.GetByTestId("import-start").ClickAsync();

            await page.GetByTestId("import-job-status-job-failed").WaitForAsync();
            await WaitForTextAsync(page.GetByTestId("import-job-status-job-failed"), "Failed");
            await page.GetByText("Service returned 500").WaitForAsync();

            await page.GetByTestId("import-job-retry-job-failed").ClickAsync();

            await page.GetByTestId("import-job-status-job-retry").WaitForAsync();
            await WaitForTextAsync(page.GetByTestId("import-job-status-job-retry"), "Completed");
        });
    }

    [Fact]
    public async Task ImportWizard_CancelJob()
    {
        await _fixture.RunAsync(nameof(ImportWizard_CancelJob), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            await StubListJobsAsync(page, Array.Empty<object>());
            await StubDiscoverAsync(page, new[]
            {
                new LayerStub(10, "Dams", "esriGeometryPoint", 8, false)
            });

            var jobStatus = 4; // InsertingFeatures

            await page.RouteAsync("**/api/v1/admin/import/geoservices/start", async route =>
            {
                const string jobId = "job-cancel";
                await FulfillJsonAsync(route, new
                {
                    jobId,
                    message = "Import queued",
                    statusUrl = $"/api/v1/admin/import/geoservices/jobs/{jobId}",
                    cancelUrl = $"/api/v1/admin/import/geoservices/jobs/{jobId}/cancel"
                }, status: 202);
            });

            await page.RouteAsync("**/api/v1/admin/import/geoservices/jobs/*/cancel", async route =>
            {
                jobStatus = 8; // Cancelled
                await FulfillJsonAsync(route, new
                {
                    jobId = "job-cancel",
                    message = "Cancelled"
                });
            });

            await page.RouteAsync("**/api/v1/admin/import/geoservices/jobs/*", async route =>
            {
                await FulfillJsonAsync(route, BuildJobPayload("job-cancel", jobStatus, 10, "dams"));
            });

            await page.GotoAsync(BuildImportUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("import-service-url").Locator("input")
                .FillAsync("https://geodata.hawaii.gov/arcgis/rest/services/Infrastructure/MapServer");
            await page.GetByTestId("import-discover").ClickAsync();
            await page.GetByTestId("import-layer-select-10").ClickAsync();
            await page.GetByTestId("import-start").ClickAsync();

            await page.GetByTestId("import-job-status-job-cancel").WaitForAsync();
            await WaitForTextAsync(page.GetByTestId("import-job-status-job-cancel"), "InsertingFeatures");

            await page.GetByTestId("import-job-cancel-job-cancel").ClickAsync();

            await WaitForTextAsync(page.GetByTestId("import-job-status-job-cancel"), "Cancelled");
        });
    }

    [Fact]
    public async Task ImportWizard_FileUploadPreviewAndImport()
    {
        await _fixture.RunAsync(nameof(ImportWizard_FileUploadPreviewAndImport), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;

            await page.RouteAsync("**/api/v1/admin/import/preview", async route =>
            {
                await FulfillJsonAsync(route, new
                {
                    format = "GeoJson",
                    totalFeatureCount = 2,
                    detectedSrid = 3857,
                    sampleProperties = new Dictionary<string, object?>
                    {
                        ["name"] = "Sample Feature",
                        ["category"] = "Test"
                    },
                    availableLayers = Array.Empty<string>()
                });
            });

            await page.RouteAsync("**/api/v1/admin/import/upload", async route =>
            {
                await FulfillJsonAsync(route, new
                {
                    success = true,
                    featureCount = 2,
                    tableName = "sample_points",
                    format = "GeoJson",
                    detectedSrid = 3857
                });
            });

            await page.GotoAsync(BuildImportUrl(baseUrl));
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "sample.geojson");
            await page.SetInputFilesAsync("[data-testid='import-file-input']", fixturePath);

            await page.GetByTestId("import-file-table").Locator("input").FillAsync("sample_points");
            await page.GetByTestId("import-file-preview").ClickAsync();

            await page.GetByTestId("import-file-preview-result").WaitForAsync();
            await page.GetByText("Sample Feature").WaitForAsync();

            await page.GetByTestId("import-file-target-srid").Locator("input").FillAsync("4326");
            await page.GetByTestId("import-file-start").ClickAsync();

            await page.GetByTestId("import-file-result").WaitForAsync();
            await page.GetByText("sample_points").WaitForAsync();
        });
    }

    private static string BuildImportUrl(string baseUrl)
        => baseUrl.TrimEnd('/') + "/import";

    private static Task StubListJobsAsync(IPage page, IEnumerable<object> jobs)
    {
        return page.RouteAsync("**/api/v1/admin/import/geoservices/jobs", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                jobs = jobs.ToArray()
            });
        });
    }

    private static Task StubDiscoverAsync(IPage page, IEnumerable<LayerStub> layers)
    {
        return page.RouteAsync("**/api/v1/admin/import/geoservices/discover", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                serviceUrl = "https://example.com/arcgis/rest/services/Stub/FeatureServer",
                serviceName = "Stub Service",
                description = "Playwright stub",
                spatialReferenceWkid = 4326,
                maxRecordCount = 1000,
                layers = layers.Select(layer => new
                {
                    id = layer.Id,
                    name = layer.Name,
                    description = layer.Description,
                    geometryType = layer.GeometryType,
                    featureCount = layer.FeatureCount,
                    hasAttachments = layer.HasAttachments
                }).ToArray()
            });
        });
    }

    private static object BuildJobPayload(
        string jobId,
        int status,
        int layerId,
        string tableName,
        string? errorMessage = null)
    {
        return new
        {
            jobId,
            status,
            featuresProcessed = status == 6 ? 120 : 12,
            estimatedTotalFeatures = 120,
            batchesCompleted = status == 6 ? 6 : 1,
            totalBatches = 6,
            failedFeatures = status == 7 ? 12 : 0,
            sourceServiceUrl = "https://example.com/arcgis/rest/services/Stub/FeatureServer",
            sourceLayerId = layerId,
            sourceLayerName = "Stub Layer",
            tableName,
            startedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            completedAt = status is 6 or 7 or 8 ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            errorMessage,
            warnings = Array.Empty<string>(),
            currentPhase = status == 4 ? "Inserting features" : null
        };
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

    private static async Task WaitForTextAsync(ILocator locator, string expected, int timeoutMs = 10_000)
    {
        await WaitForConditionAsync(
            async () =>
            {
                var text = await locator.TextContentAsync();
                return text?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;
            },
            TimeSpan.FromMilliseconds(timeoutMs),
            $"Timed out waiting for text '{expected}'.");
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

    private static string ExtractJobId(string url)
    {
        var uri = new Uri(url);
        return uri.Segments.Last().Trim('/');
    }

    private sealed record LayerStub(int Id, string Name, string GeometryType, int FeatureCount, bool HasAttachments)
    {
        public string? Description { get; init; }
    }
}
