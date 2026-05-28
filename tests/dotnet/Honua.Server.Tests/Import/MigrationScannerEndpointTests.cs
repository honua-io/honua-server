// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Server.Features.Import;
using Honua.Server.Features.Migration;
using Honua.Server.Features.FileImport;
using Honua.Server.Features.RasterImport;
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
    private readonly FakeOgcScanService _ogcService = new();
    private readonly FakeOgcApiFeaturesScanService _ogcApiFeaturesService = new();
    private HttpClient _client = null!;

    public MigrationScannerEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<IGeoServerImportService>(_geoServerService)
            .ReplaceService<IGeoservicesImportService>(_geoservicesService)
            .ReplaceService<IOgcServiceMigrationScanner>(_ogcService)
            .ReplaceService<IOgcApiFeaturesMigrationScanner>(_ogcApiFeaturesService);
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
    public async Task Scan_OgcWfsSourceWithAllArtifacts_ReturnsInventoryManifestAndEvidence()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "ogc-wfs",
            SourceUrl = "https://example.com/geoserver/wfs",
            ServiceVersion = "2.0.0",
            ArtifactSet = "all",
            TargetServiceName = "Migrated OGC"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var root = payload!.RootElement;
        root.GetProperty("inventory").GetProperty("sourceKind").GetString().Should().Be("ogc-wfs");
        root.GetProperty("manifest").GetProperty("artifactKind").GetString().Should().Be("honua.migration.manifest");
        root.GetProperty("manifest").GetProperty("targetResources").GetArrayLength().Should().Be(1);
        root.GetProperty("parityEvidence").GetProperty("artifactKind").GetString().Should().Be("honua.migration.parity-evidence-pack");
        root.GetProperty("parityEvidence").GetProperty("manifestAvailable").GetBoolean().Should().BeTrue();
        root.GetProperty("inventory").GetProperty("fidelityClassifications").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("parityEvidence").GetProperty("sections").EnumerateArray()
            .Should().Contain(section => section.GetProperty("id").GetString() == "fidelity");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_OgcWcsSourceWithAllArtifacts_ReturnsCoverageInventoryManifestAndEvidence()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "ogc-wcs",
            SourceUrl = "https://example.com/geoserver/wcs",
            ServiceVersion = "2.0.1",
            ArtifactSet = "all",
            TargetServiceName = "Migrated Coverages"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var root = payload!.RootElement;
        root.GetProperty("inventory").GetProperty("sourceKind").GetString().Should().Be("ogc-wcs");
        root.GetProperty("inventory").GetProperty("resources")[0].GetProperty("kind").GetString().Should().Be("coverage");
        root.GetProperty("manifest").GetProperty("targetResources")[0].GetProperty("migrationMode").GetString()
            .Should().Be("raster-coverage-import");
        root.GetProperty("parityEvidence").GetProperty("sections").EnumerateArray()
            .Should().Contain(section => section.GetProperty("id").GetString() == "fidelity");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_OgcApiCoveragesSource_ReturnsCoverageInventory()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "ogc-api-coverages",
            SourceUrl = "https://example.com/coverages",
            ArtifactSet = "all"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var inventory = payload!.RootElement.GetProperty("inventory");
        inventory.GetProperty("sourceKind").GetString().Should().Be("ogc-api-coverages");
        inventory.GetProperty("resources")[0].GetProperty("name").GetString().Should().Be("ocean-forecast");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_OgcWmsSource_ReturnsUnsupportedRenderOnlyInventory()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "ogc-wms",
            SourceUrl = "https://example.com/geoserver/wms",
            ServiceVersion = "1.3.0"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var resource = payload!.RootElement.GetProperty("resources")[0];
        resource.GetProperty("kind").GetString().Should().Be("render-layer");
        resource.GetProperty("compatibility").GetProperty("level").GetString().Should().Be("incompatible");
        resource.GetProperty("compatibility").GetProperty("code").GetString().Should().Be(ImportCompatibilityCodes.OgcWmsRenderOnlySource);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_OgcApiFeaturesSourceWithAllArtifacts_ReturnsInventoryManifestAndEvidence()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "ogc-api-features",
            SourceUrl = "https://example.com/ogcapi/",
            ArtifactSet = "all",
            TargetServiceName = "Migrated OAPIF"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var root = payload!.RootElement;
        root.GetProperty("inventory").GetProperty("sourceKind").GetString().Should().Be("ogc-api-features");
        root.GetProperty("inventory").GetProperty("resources")[0].GetProperty("kind").GetString()
            .Should().Be("ogc-api-features-collection");
        root.GetProperty("manifest").GetProperty("targetResources")[0].GetProperty("migrationMode").GetString()
            .Should().Be("feature-import");
        root.GetProperty("manifest").GetProperty("targetResources")[0].GetProperty("sourceProtocol").GetString()
            .Should().Be("OGC API Features");
        root.GetProperty("parityEvidence").GetProperty("sections").EnumerateArray()
            .Should().Contain(section => section.GetProperty("id").GetString() == "fidelity");

        _ogcApiFeaturesService.Requests.Should().ContainSingle()
            .Which.ServiceUrl.Should().Be("https://example.com/ogcapi/");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    public async Task Scan_GeoservicesSourceWithPlaintextToken_UsesCredentialsAndRedactsResponse()
    {
        const string accessToken = "plaintext-scan-token";

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/scan", new
        {
            SourceKind = "geoservices",
            SourceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 10,
            Credentials = new
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessToken = accessToken
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(accessToken);

        using var payload = JsonDocument.Parse(body);
        payload.RootElement.GetProperty("authPosture").GetProperty("mode").GetString()
            .Should().Be(GeoservicesAuthenticationModes.Token);
        payload.RootElement.GetProperty("authPosture").GetProperty("credentialsSupplied").GetBoolean()
            .Should().BeTrue();

        _geoservicesService.ScanRequests.Should().ContainSingle();
        _geoservicesService.ScanRequests.Single().Credentials.Should().NotBeNull();
        _geoservicesService.ScanRequests.Single().Credentials!.AccessToken.Should().Be(accessToken);
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
        public List<GeoservicesDiscoveryRequest> ScanRequests { get; } = [];

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
            ScanRequests.Add(request);
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
                    Mode = request.Credentials?.GetNormalizedMode() ?? GeoservicesAuthenticationModes.Anonymous,
                    CredentialsSupplied = request.Credentials?.HasCredentialMaterial == true,
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

    private sealed class FakeOgcScanService : IOgcServiceMigrationScanner
    {
        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            OgcServiceScanRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ServiceType.Equals("WMS", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new MigrationSourceInventoryArtifact
                {
                    SourceKind = "ogc-wms",
                    Source = new MigrationSourceIdentity
                    {
                        DisplayName = "Reference WMS",
                        BaseUrl = request.ServiceUrl,
                        Product = "OGC Web Map Service",
                        Version = request.Version,
                        ServiceType = "WMS"
                    },
                    AuthPosture = new MigrationInventoryAuthPosture
                    {
                        Mode = "anonymous",
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
                        IncompatibleCount = 1
                    },
                    OverallCompatibility = new MigrationCompatibilityAssessment
                    {
                        Level = "incompatible",
                        Code = ImportCompatibilityCodes.OgcWmsRenderOnlySource,
                        Reason = "WMS exposes rendered map images and cannot supply automated feature data-copy by itself."
                    },
                    Containers =
                    [
                        new MigrationInventoryContainer
                        {
                            Id = "service:wms",
                            Kind = "ogc-service",
                            Name = "WMS",
                            Compatibility = new MigrationCompatibilityAssessment
                            {
                                Level = "incompatible",
                                Code = ImportCompatibilityCodes.OgcWmsRenderOnlySource,
                                Reason = "Render-only service."
                            }
                        }
                    ],
                    Resources =
                    [
                        new MigrationInventoryResource
                        {
                            Id = "wms-layer:topp-states",
                            ContainerId = "service:wms",
                            Kind = "render-layer",
                            Name = "topp:states",
                            Compatibility = new MigrationCompatibilityAssessment
                            {
                                Level = "incompatible",
                                Code = ImportCompatibilityCodes.OgcWmsRenderOnlySource,
                                Reason = "WMS exposes rendered map images and cannot supply automated feature data-copy by itself."
                            }
                        }
                    ],
                    FidelityClassifications =
                    [
                        new MigrationFidelityClassificationRecord
                        {
                            Id = "classification:wms-layer:topp-states:render-data-copy",
                            SourceId = "wms-layer:topp-states",
                            Kind = "render-layer",
                            Category = "render-data-copy",
                            Name = "topp:states",
                            AutomationStatus = MigrationFidelityAutomationStatuses.Unsupported,
                            Code = ImportCompatibilityCodes.OgcWmsRenderOnlySource,
                            Reason = "WMS exposes rendered map images and cannot supply automated feature data-copy by itself.",
                            TargetKind = "map-service-layer",
                            ManualSteps = ["Pair this WMS layer with a WFS, coverage, database, or file source before planning data import."]
                        }
                    ]
                });
            }

            if (request.ServiceType.Equals("WCS", StringComparison.OrdinalIgnoreCase) ||
                request.ServiceType.Equals("OGC API Coverages", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new MigrationSourceInventoryArtifact
                {
                    SourceKind = request.ServiceType.Equals("WCS", StringComparison.OrdinalIgnoreCase)
                        ? "ogc-wcs"
                        : "ogc-api-coverages",
                    Source = new MigrationSourceIdentity
                    {
                        DisplayName = request.ServiceType.Equals("WCS", StringComparison.OrdinalIgnoreCase)
                            ? "Reference WCS"
                            : "Reference Coverages",
                        BaseUrl = request.ServiceUrl,
                        Product = request.ServiceType.Equals("WCS", StringComparison.OrdinalIgnoreCase)
                            ? "OGC Web Coverage Service"
                            : "OGC API Coverages",
                        Version = request.Version,
                        ServiceType = request.ServiceType
                    },
                    AuthPosture = new MigrationInventoryAuthPosture
                    {
                        Mode = "anonymous",
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
                        PartiallyCompatibleCount = 1
                    },
                    OverallCompatibility = new MigrationCompatibilityAssessment
                    {
                        Level = "partial",
                        Code = OgcCoverageMigrationCompatibilityCodes.CogSupported,
                        Reason = "Coverage advertises Cloud Optimized GeoTIFF output that can seed the automated raster migration path."
                    },
                    Containers =
                    [
                        new MigrationInventoryContainer
                        {
                            Id = "service:coverage",
                            Kind = "ogc-coverage-service",
                            Name = request.ServiceType,
                            Compatibility = new MigrationCompatibilityAssessment
                            {
                                Level = "partial",
                                Reason = "Coverage service contains migration-review items."
                            }
                        }
                    ],
                    Resources =
                    [
                        new MigrationInventoryResource
                        {
                            Id = request.ServiceType.Equals("WCS", StringComparison.OrdinalIgnoreCase)
                                ? "coverage:nurc-temperature"
                                : "coverage:ocean-forecast",
                            ContainerId = "service:coverage",
                            Kind = "coverage",
                            Name = request.ServiceType.Equals("WCS", StringComparison.OrdinalIgnoreCase)
                                ? "nurc:temperature"
                                : "ocean-forecast",
                            Capabilities = ["wcs:DescribeCoverage", "wcs:GetCoverage"],
                            Compatibility = new MigrationCompatibilityAssessment
                            {
                                Level = "partial",
                                Code = OgcCoverageMigrationCompatibilityCodes.CogSupported,
                                Reason = "Coverage advertises Cloud Optimized GeoTIFF output that can seed the automated raster migration path.",
                                ManualSteps = ["Run pilot coverage import and verify parity."]
                            }
                        }
                    ],
                    FidelityClassifications =
                    [
                        new MigrationFidelityClassificationRecord
                        {
                            Id = "fidelity:coverage:nurc-temperature:metadata",
                            SourceId = "coverage:nurc-temperature",
                            Kind = "coverage",
                            Category = "coverage-metadata",
                            AutomationStatus = MigrationFidelityAutomationStatuses.Assisted,
                            Code = OgcCoverageMigrationCompatibilityCodes.CogSupported,
                            Reason = "Coverage parity probes were scheduled for metadata, subset, CRS, format, no-data, and band review."
                        }
                    ]
                });
            }

            return Task.FromResult(new MigrationSourceInventoryArtifact
            {
                SourceKind = "ogc-wfs",
                Source = new MigrationSourceIdentity
                {
                    DisplayName = "Reference WFS",
                    BaseUrl = request.ServiceUrl,
                    Product = "OGC Web Feature Service",
                    Version = request.Version,
                    ServiceType = "WFS"
                },
                AuthPosture = new MigrationInventoryAuthPosture
                {
                    Mode = "anonymous",
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
                    CompatibleCount = 1
                },
                OverallCompatibility = new MigrationCompatibilityAssessment
                {
                    Level = "compatible",
                    Code = ImportCompatibilityCodes.OgcWfsFeatureSource,
                    Reason = "WFS feature type metadata can be represented in the migration inventory."
                },
                Containers =
                [
                    new MigrationInventoryContainer
                    {
                        Id = "service:wfs",
                        Kind = "ogc-service",
                        Name = "WFS",
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "WFS service is compatible."
                        }
                    }
                ],
                Resources =
                [
                    new MigrationInventoryResource
                    {
                        Id = "feature-type:topp-states",
                        ContainerId = "service:wfs",
                        Kind = "feature-type",
                        Name = "topp:states",
                        GeometryType = "Point",
                        Capabilities = ["wfs:GetFeature"],
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Code = ImportCompatibilityCodes.OgcWfsFeatureSource,
                            Reason = "WFS feature type metadata can be represented in the migration inventory."
                        }
                    }
                ],
                FidelityClassifications =
                [
                    new MigrationFidelityClassificationRecord
                    {
                        Id = "classification:feature-type:topp-states:feature-schema",
                        SourceId = "feature-type:topp-states",
                        Kind = "feature-type",
                        Category = "feature-schema",
                        Name = "topp:states",
                        AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
                        Code = ImportCompatibilityCodes.OgcWfsFeatureSource,
                        Reason = "WFS feature type metadata can be represented in the migration inventory.",
                        TargetKind = "feature-layer"
                    }
                ]
            });
        }
    }

    private sealed class FakeOgcApiFeaturesScanService : IOgcApiFeaturesMigrationScanner
    {
        public List<OgcApiFeaturesScanRequest> Requests { get; } = [];

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            OgcApiFeaturesScanRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(OgcApiFeaturesMigrationInventoryScanner.BuildInventory(new OgcApiFeaturesMigrationSourceSnapshot
            {
                BaseUrl = request.ServiceUrl,
                Title = "Reference OGC API Features",
                ConformanceClasses =
                [
                    "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                    "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson"
                ],
                Collections =
                [
                    new OgcApiFeaturesCollectionSnapshot
                    {
                        Id = "roads",
                        Title = "Roads",
                        GeometryType = "Point",
                        FeatureCount = 2,
                        Links =
                        [
                            new OgcApiFeaturesLink
                            {
                                Rel = "items",
                                Href = "https://example.com/ogcapi/collections/roads/items",
                                Type = "application/geo+json"
                            },
                            new OgcApiFeaturesLink
                            {
                                Rel = "queryables",
                                Href = "https://example.com/ogcapi/collections/roads/queryables",
                                Type = "application/schema+json"
                            },
                            new OgcApiFeaturesLink
                            {
                                Rel = "describedby",
                                Href = "https://example.com/ogcapi/collections/roads/schema",
                                Type = "application/schema+json"
                            }
                        ],
                        PaginationLinks =
                        [
                            new OgcApiFeaturesLink
                            {
                                Rel = "next",
                                Href = "https://example.com/ogcapi/collections/roads/items?offset=1&limit=1",
                                Type = "application/geo+json"
                            }
                        ],
                        CrsDeclarations =
                        [
                            new OgcApiFeaturesCrsDeclaration
                            {
                                Role = "storage",
                                Value = "http://www.opengis.net/def/crs/EPSG/0/4326"
                            }
                        ],
                        ItemEncodings = ["application/geo+json"],
                        Fields =
                        [
                            new MigrationInventoryField
                            {
                                Name = "name",
                                FieldType = "string"
                            }
                        ]
                    }
                ]
            }));
        }
    }
}
