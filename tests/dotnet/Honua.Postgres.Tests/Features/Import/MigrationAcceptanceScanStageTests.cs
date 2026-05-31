// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Drives the migration acceptance suite scan stage end-to-end across one ArcGIS GeoServices REST
/// fixture, one GeoServer REST fixture, and one OGC API Features snapshot fixture. Asserts that the
/// scan stage emits a deterministic <see cref="MigrationScanStageReport"/> that respects the
/// artifact contracts shipped by issue #1024 slice 1 (no credentials in artifact output, unsupported
/// items are classified, deterministic re-run produces identical output).
/// </summary>
public sealed class MigrationAcceptanceScanStageTests
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public async Task ScanStage_AcrossArcGisGeoServerAndOgcApiFeatures_ProducesDeterministicReport()
    {
        var first = await BuildReportAsync();
        var second = await BuildReportAsync();

        first.RunId.Should().Be("acceptance-scan-stage:slice-2");
        first.ArtifactKind.Should().Be("honua.migration.scan-stage-report");
        first.ArtifactVersion.Should().Be("1.0");

        var firstJson = JsonSerializer.Serialize(first, ArtifactJsonOptions);
        var secondJson = JsonSerializer.Serialize(second, ArtifactJsonOptions);
        secondJson.Should().Be(firstJson, "the scan stage must be deterministic across re-runs of the same fixture set.");
    }

    [Fact]
    public async Task ScanStage_EmitsOneInventoryArtifactPerFixtureSource()
    {
        var report = await BuildReportAsync();

        report.Sources.Should().HaveCount(3);
        report.Summary.SourceCount.Should().Be(3);

        report.Sources.Select(entry => entry.FixtureId)
            .Should().BeInAscendingOrder(StringComparer.Ordinal)
            .And.Equal(
                "arcgis-mapserver-mixed-renderers",
                "geoserver-mixed-catalog",
                "ogc-api-features-demo");

        report.Sources.Select(entry => entry.SourceKind)
            .Should().Equal(
                "arcgis-geoservices-rest",
                "geoserver-rest",
                "ogc-api-features");

        foreach (var entry in report.Sources)
        {
            entry.Inventory.Should().NotBeNull();
            entry.Inventory.ArtifactKind.Should().Be("honua.migration.source-inventory");
            entry.Inventory.ArtifactVersion.Should().Be("1.0");
        }
    }

    [Fact]
    public async Task ScanStage_ClassifiesUnsupportedItems_AcrossSources()
    {
        var report = await BuildReportAsync();

        // The ArcGIS MapServer-MixedRenderers fixture exercises an explicitly unsupported renderer
        // (heatmap) — the scan stage must surface it in the rollup so manifest/apply stages can
        // refuse to silently auto-migrate destructive or unsupported source items.
        report.Summary.UnsupportedCount.Should().BeGreaterThan(0,
            "the MapServer-MixedRenderers fixture intentionally includes an unsupported renderer.");

        var arcgisEntry = report.Sources.Single(entry => entry.FixtureId == "arcgis-mapserver-mixed-renderers");
        arcgisEntry.Inventory.FidelityClassifications
            .Should().Contain(record => record.AutomationStatus == MigrationFidelityAutomationStatuses.Unsupported);

        // The GeoServer MixedCatalog fixture includes an unsupported store and a manual-review
        // coverage store — both must be surfaced as non-automated classifications.
        var geoServerEntry = report.Sources.Single(entry => entry.FixtureId == "geoserver-mixed-catalog");
        geoServerEntry.Inventory.ExternalDependencies
            .Should().Contain(dependency => dependency.Compatibility.Level == "incompatible");
        geoServerEntry.Inventory.ExternalDependencies
            .Should().Contain(dependency => dependency.Compatibility.Level == "partial");
    }

    [Fact]
    public async Task ScanStage_RedactsCredentials_FromArtifactOutput()
    {
        var report = await BuildReportAsync();

        // The ArcGIS GeoServices request body sets a token query string when supplied. Even when
        // the secure fixture path is exercised, the artifact must never echo the credential back.
        var json = JsonSerializer.Serialize(report, ArtifactJsonOptions);

        json.Should().NotContain("super-secret-token",
            "the inventory artifact must never echo back the supplied ArcGIS token.");
        json.Should().NotContain("Authorization",
            "the inventory artifact must not surface raw HTTP authorization headers.");
        json.Should().NotContain("password",
            "the inventory artifact must not surface password values from the source.");
        json.Should().NotContain("Basic ",
            "the inventory artifact must not surface raw Basic auth values from the source.");
    }

    [Fact]
    public async Task ScanStage_SummaryCounts_MatchSumOfPerSourceInventories()
    {
        var report = await BuildReportAsync();

        var expectedContainers = report.Sources.Sum(entry => entry.Inventory.Summary.ContainerCount);
        var expectedResources = report.Sources.Sum(entry => entry.Inventory.Summary.ResourceCount);
        var expectedStyles = report.Sources.Sum(entry => entry.Inventory.Summary.StyleCount);
        var expectedExternal = report.Sources.Sum(entry => entry.Inventory.Summary.ExternalDependencyCount);

        report.Summary.ContainerCount.Should().Be(expectedContainers);
        report.Summary.ResourceCount.Should().Be(expectedResources);
        report.Summary.StyleCount.Should().Be(expectedStyles);
        report.Summary.ExternalDependencyCount.Should().Be(expectedExternal);

        var expectedAutomated = report.Sources
            .SelectMany(entry => entry.Inventory.FidelityClassifications)
            .Count(record => record.AutomationStatus == MigrationFidelityAutomationStatuses.Automated);
        report.Summary.AutomatedCount.Should().Be(expectedAutomated);
    }

    private static async Task<MigrationScanStageReport> BuildReportAsync()
    {
        var arcgisInventory = await ScanArcGisAsync();
        var geoServerInventory = await ScanGeoServerAsync();
        var ogcInventory = ScanOgcApiFeatures();

        var report = MigrationAcceptanceScanStageRunner.BuildReport(
            "acceptance-scan-stage:slice-2",
            [
                new MigrationAcceptanceScanStageInput
                {
                    FixtureId = "arcgis-mapserver-mixed-renderers",
                    Inventory = arcgisInventory
                },
                new MigrationAcceptanceScanStageInput
                {
                    FixtureId = "geoserver-mixed-catalog",
                    Inventory = geoServerInventory
                },
                new MigrationAcceptanceScanStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = ogcInventory
                }
            ]);

        return report;
    }

    private static async Task<MigrationSourceInventoryArtifact> ScanArcGisAsync()
    {
        var fixture = LoadFixture("ArcGis", "MapServer-MixedRenderers");
        var service = CreateGeoservicesService(new FixtureHttpHandler(fixture.Responses));

        return await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = fixture.ServiceUrl,
            TimeoutSeconds = 5
        });
    }

    private static async Task<MigrationSourceInventoryArtifact> ScanGeoServerAsync()
    {
        var fixture = LoadFixture("GeoServer", "MixedCatalog");
        var service = CreateGeoServerService(new FixtureHttpHandler(fixture.Responses));

        return await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            IncludeCompatibilityAnalysis = true,
            IncludeStyleContent = true,
            TimeoutSeconds = 5
        });
    }

    private static MigrationSourceInventoryArtifact ScanOgcApiFeatures()
    {
        // OGC API Features inventories are built from a captured snapshot. The snapshot here is the
        // "recorded fixture" for slice 2 — it intentionally mirrors a realistic public deployment so
        // downstream stages can be exercised without live network access.
        var snapshot = new OgcApiFeaturesMigrationSourceSnapshot
        {
            BaseUrl = "https://demo.example/ogcapi/",
            Title = "Demo OGC API Features",
            Version = "1.0",
            LandingPageLinks =
            [
                Link("service-desc", "https://demo.example/ogcapi/openapi", "application/vnd.oai.openapi+json;version=3.0"),
                Link("conformance", "https://demo.example/ogcapi/conformance", "application/json"),
                Link("data", "https://demo.example/ogcapi/collections", "application/json")
            ],
            ConformanceClasses =
            [
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables"
            ],
            Collections =
            [
                new OgcApiFeaturesCollectionSnapshot
                {
                    Id = "roads",
                    Title = "Roads",
                    GeometryType = "LineString",
                    FeatureCount = 125,
                    Links =
                    [
                        Link("items", "https://demo.example/ogcapi/collections/roads/items", "application/geo+json"),
                        Link("http://www.opengis.net/def/rel/ogc/1.0/queryables", "https://demo.example/ogcapi/collections/roads/queryables", "application/schema+json")
                    ],
                    CrsDeclarations =
                    [
                        Crs("storage", "http://www.opengis.net/def/crs/OGC/1.3/CRS84"),
                        Crs("supported", "http://www.opengis.net/def/crs/EPSG/0/4326")
                    ],
                    ItemEncodings = ["application/geo+json"],
                    Fields =
                    [
                        new MigrationInventoryField
                        {
                            Name = "name",
                            Alias = "Road name",
                            FieldType = "string",
                            Nullable = false
                        }
                    ]
                }
            ]
        };

        return OgcApiFeaturesMigrationInventoryScanner.BuildInventory(snapshot);
    }

    private static OgcApiFeaturesLink Link(string rel, string href, string? type = null) =>
        new() { Rel = rel, Href = href, Type = type };

    private static OgcApiFeaturesCrsDeclaration Crs(string role, string value) =>
        new() { Role = role, Value = value };

    private static FixtureScenario LoadFixture(string family, string scenario)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "Import",
            "Fixtures",
            family,
            $"{scenario}.json");

        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = document.RootElement;
        var serviceUrl = root.GetProperty("serviceUrl").GetString()
            ?? throw new InvalidDataException($"Fixture {scenario} is missing serviceUrl.");
        var responses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("responses").EnumerateObject())
        {
            responses[entry.Name] = entry.Value.ValueKind == JsonValueKind.String
                ? entry.Value.GetString() ?? string.Empty
                : entry.Value.GetRawText();
        }

        return new FixtureScenario(serviceUrl, responses);
    }

    private static GeoservicesImportService CreateGeoservicesService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new ArcGisRestClient(
            httpClient,
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = CreateCrsRegistry();

        return new GeoservicesImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance);
    }

    private static GeoServerImportService CreateGeoServerService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = CreateCrsRegistry();

        return new GeoServerImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry,
            NullLogger<GeoServerImportService>.Instance);
    }

    private static ICrsRegistry CreateCrsRegistry()
    {
        var registry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int srid, CancellationToken _) => new ValueTask<CrsDefinition?>(
                srid switch
                {
                    3857 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false),
                    4326 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/4326", 4326, AxisOrder.EastNorth, true),
                    _ => null
                }));
        return registry.Object;
    }

    private sealed record FixtureScenario(string ServiceUrl, IReadOnlyDictionary<string, string> Responses);

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;

        public FixtureHttpHandler(IReadOnlyDictionary<string, string> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (!_responses.TryGetValue(pathAndQuery, out var body))
            {
                throw new InvalidOperationException(
                    $"Fixture has no response for {pathAndQuery}. Add it to the fixture JSON or correct the request path.");
            }

            var contentType = pathAndQuery.EndsWith(".xml", StringComparison.Ordinal)
                ? "application/xml"
                : pathAndQuery.EndsWith(".sld", StringComparison.Ordinal)
                    ? "application/vnd.ogc.sld+xml"
                    : "application/json";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }
}
