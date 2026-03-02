// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class MetadataPageTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] _metadataApiVersions = ["honua.io/v1", "honua.io/v1alpha1"];
    private static readonly string[] _resourceKinds = ["Layer", "Service"];
    private readonly PlaywrightFixture _fixture;

    public MetadataPageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MetadataPage_LoadsManifestAndResources()
    {
        await _fixture.RunAsync(nameof(MetadataPage_LoadsManifestAndResources), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;

            await StubMetadataEndpointsAsync(page, now);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/metadata");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByText("Metadata").WaitForAsync();
            await page.GetByText("Metadata API: honua.io/v1").WaitForAsync();
            await page.GetByText("Drifted resources: 1").WaitForAsync();
            await page.GetByTestId("metadata-resources-table").WaitForAsync();
            await page.GetByText("parcels-layer").WaitForAsync();
        });
    }

    [Fact]
    public async Task MetadataPage_ManifestFailure_ShowsError()
    {
        await _fixture.RunAsync(nameof(MetadataPage_ManifestFailure_ShowsError), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;
            await StubMetadataEndpointsAsync(page, now, manifestSuccess: false);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/metadata");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByText("Failed to load metadata manifest").WaitForAsync();
        });
    }

    [Fact]
    public async Task MetadataPage_ApplyManifestDryRun_ShowsSummary()
    {
        await _fixture.RunAsync(nameof(MetadataPage_ApplyManifestDryRun_ShowsSummary), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;
            await StubMetadataEndpointsAsync(page, now);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/metadata");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("metadata-manifest-dry-run").ClickAsync();
            await page.GetByText("Manifest dry run completed.").WaitForAsync();
            await page.GetByText("Created: 1, Updated: 0, Deleted: 0, Skipped: 0").WaitForAsync();
        });
    }

    [Fact]
    public async Task MetadataPage_DeleteResource_UsesEtagFlow()
    {
        await _fixture.RunAsync(nameof(MetadataPage_DeleteResource_UsesEtagFlow), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var page = ctx.Page;
            var now = DateTimeOffset.UtcNow;
            await StubMetadataEndpointsAsync(page, now);

            await page.GotoAsync($"{baseUrl.TrimEnd('/')}/metadata");
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                return;
            }

            await page.GetByTestId("metadata-delete-resource").First.ClickAsync();
            await page.GetByText("Resource deleted.").WaitForAsync();
        });
    }

    private static async Task StubMetadataEndpointsAsync(IPage page, DateTimeOffset now, bool manifestSuccess = true)
    {
        await page.RouteAsync("**/api/v1/admin/version", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new
                {
                    version = "1.0.0",
                    metadataApiVersion = "honua.io/v1",
                    serverTime = now
                }
            });
        });

        await page.RouteAsync("**/api/v1/admin/capabilities", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new
                {
                    metadataApiVersions = _metadataApiVersions,
                    resourceKinds = _resourceKinds,
                    manifestSupported = true,
                    manifestDryRunSupported = true,
                    manifestPruneSupported = true
                }
            });
        });

        await page.RouteAsync("**/api/v1/admin/manifest", async route =>
        {
            if (!manifestSuccess)
            {
                await FulfillJsonAsync(route, new
                {
                    success = false,
                    message = "Failed to load metadata manifest",
                    timestamp = now,
                    data = (object?)null
                }, status: 500);
                return;
            }

            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new
                {
                    apiVersion = "honua.io/v1",
                    generatedAt = now,
                    resources = new[]
                    {
                        new
                        {
                            apiVersion = "honua.io/v1",
                            kind = "Layer",
                            metadata = new
                            {
                                name = "parcels-layer",
                                @namespace = "default",
                                resourceVersion = "2",
                                updatedAt = now
                            },
                            spec = new { tableName = "parcels", schemaName = "public" }
                        }
                    },
                    driftedResources = new[]
                    {
                        new
                        {
                            kind = "Layer",
                            @namespace = "default",
                            name = "parcels-layer"
                        }
                    },
                    manifestHash = "abc123"
                }
            });
        });

        await page.RouteAsync("**/api/v1/admin/manifest/apply", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new
                {
                    dryRun = true,
                    summary = new
                    {
                        created = 1,
                        updated = 0,
                        deleted = 0,
                        skipped = 0
                    },
                    entries = new[]
                    {
                        new
                        {
                            action = "create",
                            resource = new
                            {
                                kind = "Layer",
                                @namespace = "default",
                                name = "parcels-layer"
                            },
                            message = "Would create resource."
                        }
                    }
                }
            });
        });

        await page.RouteAsync("**/api/v1/admin/metadata/resources", async route =>
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
                        apiVersion = "honua.io/v1",
                        kind = "Layer",
                        metadata = new
                        {
                            name = "parcels-layer",
                            @namespace = "default",
                            resourceVersion = "2",
                            updatedAt = now
                        },
                        spec = new { tableName = "parcels", schemaName = "public" }
                    }
                }
            });
        });

        await page.RouteAsync("**/api/v1/admin/metadata/resources/Layer/default/parcels-layer", async route =>
        {
            if (route.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                await FulfillJsonAsync(route, new
                {
                    success = true,
                    message = "Resource deleted.",
                    timestamp = now,
                    data = new { }
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
                    apiVersion = "honua.io/v1",
                    kind = "Layer",
                    metadata = new
                    {
                        name = "parcels-layer",
                        @namespace = "default",
                        resourceVersion = "2",
                        updatedAt = now
                    },
                    spec = new { tableName = "parcels", schemaName = "public" }
                }
            }, headers: new Dictionary<string, string> { ["ETag"] = "\"etag-2\"" });
        });
    }

    private static async Task FulfillJsonAsync(
        IRoute route,
        object payload,
        int status = 200,
        Dictionary<string, string>? headers = null)
    {
        var body = JsonSerializer.Serialize(payload, _jsonOptions);
        await route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = body,
            Headers = headers
        });
    }
}
