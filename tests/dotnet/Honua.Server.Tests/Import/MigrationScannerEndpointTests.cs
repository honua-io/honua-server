// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for the unified migration source scanner endpoint.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class MigrationScannerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private readonly FakeGeoServerScanService _geoServerService = new();
    private readonly FakeGeoservicesScanService _geoservicesService = new();
    private HttpClient _client = null!;

    public MigrationScannerEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<IGeoServerImportService>(_geoServerService)
            .ReplaceService<IGeoservicesImportService>(_geoservicesService);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_WithMissingSourceKind_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceUrl = "https://example.com/geoserver/rest"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SourceKind");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_GeoServerSource_ReturnsNormalizedInventoryArtifact()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "geoserver",
            SourceUrl = "https://example.com/geoserver/rest",
            Username = "admin",
            Password = "geoserver",
            IncludeStyleContent = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var root = payload!.RootElement;
        root.GetProperty("artifactKind").GetString().Should().Be("honua.migration.source-inventory");
        root.GetProperty("sourceKind").GetString().Should().Be("geoserver-rest");
        root.GetProperty("source").GetProperty("product").GetString().Should().Be("GeoServer");
        root.GetProperty("authPosture").GetProperty("mode").GetString().Should().Be("basic");
        root.GetProperty("containers").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("resources").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("styles").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("externalDependencies").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_GeoservicesSource_ReturnsNormalizedInventoryArtifact()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "geoservices",
            SourceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 10
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var root = payload!.RootElement;
        root.GetProperty("sourceKind").GetString().Should().Be("arcgis-geoservices-rest");
        root.GetProperty("source").GetProperty("serviceType").GetString().Should().Be("FeatureServer");
        root.GetProperty("authPosture").GetProperty("mode").GetString().Should().Be("anonymous");
        root.GetProperty("containers")[0].GetProperty("kind").GetString().Should().Be("service");
        root.GetProperty("resources")[0].GetProperty("kind").GetString().Should().Be("layer");
        root.GetProperty("styles")[0].GetProperty("kind").GetString().Should().Be("renderer");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_WithInvalidGeoservicesUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "geoservices",
            SourceUrl = "http://example.com/arcgis/rest/services/Parcels/FeatureServer"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("valid HTTPS");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_WithNonPositiveTimeout_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "geoservices",
            SourceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("TimeoutSeconds must be greater than 0");
        _geoServerService.ScanRequestCount.Should().Be(0);
        _geoservicesService.ScanRequestCount.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_GeoservicesSourceWithJsonExport_ReturnsIndentedAttachment()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/import/scan?export=json",
            new
            {
                SourceKind = "geoservices",
                SourceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
                Username = "scanner-user",
                Password = "scanner-password",
                TimeoutSeconds = 10
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition.Should().NotBeNull();
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName?.Trim('"').Should().Be("Parcels-inventory.json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("  \"artifactKind\":");
        body.Should().NotContain("scanner-password");
        body.Should().NotContain("scanner-user");
    }

    [Theory]
    [InlineData("Parcel Viewer", "Parcel-Viewer")]
    [InlineData("Demo / Service ° (alpha)", "Demo-Service-alpha")]
    [InlineData("   ", "inventory")]
    [InlineData(null, "inventory")]
    [InlineData("///", "inventory")]
    [InlineData("a___---___b", "a-b")]
    public void SanitizeFilenameSlug_ProducesSafeAsciiOnlyName(string? input, string expected)
    {
        MigrationScannerEndpoints.SanitizeFilenameSlug(input).Should().Be(expected);
    }

    [Fact]
    public void SanitizeFilenameSlug_LongInput_TrimmedAtBoundary()
    {
        var input = new string('a', 100);
        MigrationScannerEndpoints.SanitizeFilenameSlug(input).Length.Should().BeLessOrEqualTo(64);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_GeoservicesSourceWithJsonExport_ProducesStableInventoryBaseline()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/import/scan?export=json",
            new
            {
                SourceKind = "geoservices",
                SourceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
                TimeoutSeconds = 10
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var first = await response.Content.ReadAsStringAsync();

        var second = await (await _client.PostAsJsonAsync(
            "/api/v1/admin/import/scan?export=json",
            new
            {
                SourceKind = "geoservices",
                SourceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
                TimeoutSeconds = 10
            })).Content.ReadAsStringAsync();

        first.Should().Be(second, "successive exports of the same artifact must be byte-for-byte identical for downstream review tooling.");

        using var document = JsonDocument.Parse(first);
        document.RootElement.GetProperty("sourceKind").GetString().Should().Be("arcgis-geoservices-rest");
        document.RootElement.GetProperty("source").GetProperty("displayName").GetString().Should().Be("Parcels");
    }

    private sealed class FakeGeoServerScanService : IGeoServerImportService
    {
        public int ScanRequestCount { get; private set; }

        public Task<GeoServerServiceInfo> DiscoverServiceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeoServerServiceInfo
            {
                GeoServerRestUrl = request.GeoServerRestUrl,
                Version = "2.28.0"
            });

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            ScanRequestCount++;
            return Task.FromResult(new MigrationSourceInventoryArtifact
            {
                SourceKind = "geoserver-rest",
                Source = new MigrationSourceIdentity
                {
                    DisplayName = "Demo GeoServer",
                    BaseUrl = request.GeoServerRestUrl,
                    Product = "GeoServer",
                    Version = "2.28.0",
                    ServiceType = "REST"
                },
                AuthPosture = new MigrationInventoryAuthPosture
                {
                    Mode = "basic",
                    CredentialsSupplied = true,
                    AccessConfirmed = true
                },
                ScanCompleteness = new MigrationInventoryCompleteness
                {
                    Status = "complete"
                },
                Summary = new MigrationInventorySummary
                {
                    ContainerCount = 1,
                    ResourceCount = 1,
                    StyleCount = 1,
                    ExternalDependencyCount = 1,
                    CompatibleCount = 4,
                    PartiallyCompatibleCount = 0,
                    IncompatibleCount = 0
                },
                OverallCompatibility = new MigrationCompatibilityAssessment
                {
                    Level = "compatible",
                    Reason = "All scanned inventory items are compatible."
                },
                Containers =
                [
                    new MigrationInventoryContainer
                    {
                        Id = "workspace:demo",
                        Kind = "workspace",
                        Name = "demo",
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "Workspace resources are compatible."
                        }
                    }
                ],
                Resources =
                [
                    new MigrationInventoryResource
                    {
                        Id = "layer:demo:states",
                        ContainerId = "workspace:demo",
                        Kind = "layer",
                        Name = "states",
                        Capabilities = ["query"],
                        StyleIds = ["style:demo:polygon"],
                        ExternalDependencyIds = ["datastore:demo:states"],
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "Layer can be migrated."
                        }
                    }
                ],
                Styles =
                [
                    new MigrationInventoryStyle
                    {
                        Id = "style:demo:polygon",
                        ContainerId = "workspace:demo",
                        Kind = "style",
                        Name = "polygon",
                        Format = "sld",
                        ResourceIds = ["layer:demo:states"],
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "Style metadata was discovered."
                        }
                    }
                ],
                ExternalDependencies =
                [
                    new MigrationExternalDependency
                    {
                        Id = "datastore:demo:states",
                        ContainerId = "workspace:demo",
                        Kind = "datastore",
                        Name = "states",
                        DependencyType = "PostGIS",
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "Dependency metadata was discovered."
                        }
                    }
                ]
            });
        }

        public Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GeoServerImportResult.CreateSuccess(
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                sourceGeoServerVersion: "2.28.0"));

        public Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            IProgress<GeoServerImportProgress>? progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GeoServerImportResult.CreateSuccess(
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                sourceGeoServerVersion: "2.28.0"));
    }

    private sealed class FakeGeoservicesScanService : IGeoservicesImportService
    {
        public int ScanRequestCount { get; private set; }

        public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeoservicesServiceInfo
            {
                ServiceUrl = request.ServiceUrl,
                ServiceName = "Parcels"
            });

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            ScanRequestCount++;
            return Task.FromResult(new MigrationSourceInventoryArtifact
            {
                SourceKind = "arcgis-geoservices-rest",
                Source = new MigrationSourceIdentity
                {
                    DisplayName = "Parcels",
                    BaseUrl = request.ServiceUrl,
                    Product = "ArcGIS GeoServices REST",
                    Version = "11.2",
                    ServiceType = "FeatureServer"
                },
                AuthPosture = new MigrationInventoryAuthPosture
                {
                    Mode = "anonymous",
                    CredentialsSupplied = false,
                    AccessConfirmed = true
                },
                ScanCompleteness = new MigrationInventoryCompleteness
                {
                    Status = "complete"
                },
                Summary = new MigrationInventorySummary
                {
                    ContainerCount = 1,
                    ResourceCount = 1,
                    StyleCount = 1,
                    ExternalDependencyCount = 1,
                    CompatibleCount = 1,
                    PartiallyCompatibleCount = 2,
                    IncompatibleCount = 0
                },
                OverallCompatibility = new MigrationCompatibilityAssessment
                {
                    Level = "partial",
                    Reason = "Renderer recreation is required."
                },
                Containers =
                [
                    new MigrationInventoryContainer
                    {
                        Id = "service:Parcels",
                        Kind = "service",
                        Name = "Parcels",
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "partial",
                            Reason = "Service contains partially compatible items."
                        }
                    }
                ],
                Resources =
                [
                    new MigrationInventoryResource
                    {
                        Id = "resource:Parcels:layer:0",
                        ContainerId = "service:Parcels",
                        Kind = "layer",
                        Name = "Parcels",
                        GeometryType = "esriGeometryPolygon",
                        FeatureCount = 12,
                        Capabilities = ["Query"],
                        StyleIds = ["renderer:Parcels:0"],
                        ExternalDependencyIds = ["renderer:Parcels:0:external:https://example.com/symbol.png"],
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "Layer data is queryable."
                        }
                    }
                ],
                Styles =
                [
                    new MigrationInventoryStyle
                    {
                        Id = "renderer:Parcels:0",
                        ContainerId = "service:Parcels",
                        Kind = "renderer",
                        Name = "Parcels",
                        Format = "esri-renderer",
                        ResourceIds = ["resource:Parcels:layer:0"],
                        ExternalDependencyIds = ["renderer:Parcels:0:external:https://example.com/symbol.png"],
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "partial",
                            Reason = "Renderer recreation is required."
                        }
                    }
                ],
                ExternalDependencies =
                [
                    new MigrationExternalDependency
                    {
                        Id = "renderer:Parcels:0:external:https://example.com/symbol.png",
                        ContainerId = "service:Parcels",
                        ResourceId = "resource:Parcels:layer:0",
                        Kind = "external-symbol",
                        Name = "Parcels",
                        DependencyType = "url",
                        Address = "https://example.com/symbol.png",
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "partial",
                            Reason = "External symbol asset must be mirrored."
                        }
                    }
                ]
            });
        }

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GeoservicesImportResult.CreateSuccess(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featureCount: 0));

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            IProgress<GeoservicesImportProgress>? progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GeoservicesImportResult.CreateSuccess(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featureCount: 0));
    }
}
