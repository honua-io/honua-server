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
/// Drives the migration acceptance suite apply stage end-to-end across the same fixture set used by
/// <see cref="MigrationAcceptanceScanStageTests"/>: one ArcGIS GeoServices REST fixture, one
/// GeoServer REST fixture, and one OGC API Features snapshot fixture. The apply stage runs scan ->
/// manifest -> apply (dry-run for fixtures whose apply path is not yet supported against the
/// fixture target) and asserts that the slice-3 <see cref="MigrationApplyStageReport"/> emits the
/// <see cref="MigrationManifestArtifact"/> per fixture deterministically while preserving the
/// slice-1 redaction and manual-review-classification contracts.
/// </summary>
public sealed class MigrationAcceptanceApplyStageTests
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public async Task ApplyStage_AcrossArcGisGeoServerAndOgcApiFeatures_ProducesDeterministicReport()
    {
        var first = await BuildReportAsync();
        var second = await BuildReportAsync();

        first.RunId.Should().Be("acceptance-apply-stage:slice-3");
        first.ScanRunId.Should().Be("acceptance-scan-stage:slice-2");
        first.ArtifactKind.Should().Be("honua.migration.apply-stage-report");
        first.ArtifactVersion.Should().Be("1.0");

        var firstJson = JsonSerializer.Serialize(first, ArtifactJsonOptions);
        var secondJson = JsonSerializer.Serialize(second, ArtifactJsonOptions);
        secondJson.Should().Be(firstJson, "the apply stage must be deterministic across re-runs of the same fixture set.");

        foreach (var entry in first.Sources)
        {
            entry.Outcome.ReplayToken.Should().StartWith("sha256:");
        }
    }

    [Fact]
    public async Task ApplyStage_EmitsOneManifestArtifactPerFixtureSource()
    {
        var report = await BuildReportAsync();

        report.Sources.Should().HaveCount(3);
        report.Summary.SourceCount.Should().Be(3);
        // All three fixture families currently use the deterministic dry-run path: none of the
        // import services exposed here can drive a non-destructive apply against the fixture
        // target without a live database connection, so the slice gates the apply stage on the
        // apply-plan replay token. The dry-run path still emits the full manifest contract.
        report.Summary.DryRunSourceCount.Should().Be(3);
        report.Summary.AppliedSourceCount.Should().Be(0);

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
            entry.Outcome.Manifest.Should().NotBeNull();
            entry.Outcome.Manifest.ArtifactKind.Should().Be("honua.migration.manifest");
            entry.Outcome.Manifest.ArtifactVersion.Should().Be("1.0");
            entry.Outcome.Manifest.SourceKind.Should().Be(entry.SourceKind);
        }
    }

    [Fact]
    public async Task ApplyStage_ChainsScanThenApply_ForSingleFixture()
    {
        // A single test that drives scan -> apply for one fixture and asserts both artifacts emit
        // deterministically — exercising the per-fixture path through the acceptance suite.
        var inventory = ScanOgcApiFeatures();

        var scanReport = MigrationAcceptanceScanStageRunner.BuildReport(
            "acceptance-scan-stage:slice-2:ogc-only",
            [
                new MigrationAcceptanceScanStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory
                }
            ]);

        var applyReport = MigrationAcceptanceApplyStageRunner.BuildReport(
            "acceptance-apply-stage:slice-3:ogc-only",
            scanReport.RunId,
            [
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = scanReport.Sources.Single().Inventory
                }
            ]);

        scanReport.Sources.Should().ContainSingle();
        applyReport.Sources.Should().ContainSingle();
        applyReport.Sources.Single().FixtureId.Should().Be(scanReport.Sources.Single().FixtureId);
        applyReport.Sources.Single().Outcome.Manifest.SourceKind
            .Should().Be(scanReport.Sources.Single().SourceKind);

        var replayed = MigrationAcceptanceApplyStageRunner.BuildReport(
            applyReport.RunId,
            applyReport.ScanRunId,
            [
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = scanReport.Sources.Single().Inventory
                }
            ]);

        var first = JsonSerializer.Serialize(applyReport, ArtifactJsonOptions);
        var second = JsonSerializer.Serialize(replayed, ArtifactJsonOptions);
        second.Should().Be(first, "scan -> apply for a single fixture must replay deterministically.");
    }

    [Fact]
    public async Task ApplyStage_ClassifiesManualReviewAndUnsupportedItems_AcrossSources()
    {
        var report = await BuildReportAsync();

        // The ArcGIS MapServer-MixedRenderers fixture intentionally exercises an unsupported
        // renderer (heatmap). The apply stage must classify the offending source item as
        // manual-review or unsupported instead of silently auto-staging it.
        var arcgisEntry = report.Sources.Single(entry => entry.FixtureId == "arcgis-mapserver-mixed-renderers");
        arcgisEntry.Outcome.UnsupportedItemCount.Should().BeGreaterThan(0,
            "the MapServer-MixedRenderers fixture intentionally includes an unsupported renderer.");
        arcgisEntry.Outcome.Classifications
            .Should().Contain(classification => classification.Disposition == "unsupported");
        arcgisEntry.Outcome.Diagnostics
            .Should().Contain(diagnostic => diagnostic.Severity == "unsupported");

        // The GeoServer MixedCatalog fixture has a manual-review coverage store. The apply stage
        // must route those items through the manual-review classification path so they are not
        // auto-applied.
        var geoServerEntry = report.Sources.Single(entry => entry.FixtureId == "geoserver-mixed-catalog");
        (geoServerEntry.Outcome.ManualReviewItemCount + geoServerEntry.Outcome.UnsupportedItemCount)
            .Should().BeGreaterThan(0, "the MixedCatalog fixture intentionally includes non-automated items.");
        geoServerEntry.Outcome.Diagnostics.Should().NotBeEmpty();
        geoServerEntry.Outcome.ManualReviewItems
            .Should().BeInAscendingOrder(item => item.SourceId, StringComparer.Ordinal);

        // Summary counts must match the per-source rollups exactly.
        report.Summary.AppliedItemCount.Should()
            .Be(report.Sources.Sum(entry => entry.Outcome.AppliedItemCount));
        report.Summary.ManualReviewItemCount.Should()
            .Be(report.Sources.Sum(entry => entry.Outcome.ManualReviewItemCount));
        report.Summary.UnsupportedItemCount.Should()
            .Be(report.Sources.Sum(entry => entry.Outcome.UnsupportedItemCount));
        report.Summary.DiagnosticCount.Should()
            .Be(report.Sources.Sum(entry => entry.Outcome.Diagnostics.Length));
    }

    [Fact]
    public async Task ApplyStage_RedactsCredentials_FromArtifactOutput()
    {
        var report = await BuildReportAsync();

        var json = JsonSerializer.Serialize(report, ArtifactJsonOptions);

        json.Should().NotContain("super-secret-token",
            "the apply stage manifest must never echo back the supplied ArcGIS token.");
        json.Should().NotContain("Authorization",
            "the apply stage manifest must not surface raw HTTP authorization headers.");
        json.Should().NotContain("password",
            "the apply stage manifest must not surface password values from the source.");
        json.Should().NotContain("Basic ",
            "the apply stage manifest must not surface raw Basic auth values from the source.");
    }

    [Fact]
    public async Task ApplyStage_DeterministicReapply_MatchesReplayToken_PerSource()
    {
        var first = await BuildReportAsync();
        var second = await BuildReportAsync();

        first.Sources.Should().HaveSameCount(second.Sources);
        foreach (var (left, right) in first.Sources.Zip(second.Sources))
        {
            left.FixtureId.Should().Be(right.FixtureId);
            left.Outcome.ReplayToken.Should().Be(right.Outcome.ReplayToken,
                $"the apply stage replay token for fixture '{left.FixtureId}' must be stable across re-runs.");
        }
    }

    private static async Task<MigrationApplyStageReport> BuildReportAsync()
    {
        var arcgisInventory = await ScanArcGisAsync();
        var geoServerInventory = await ScanGeoServerAsync();
        var ogcInventory = ScanOgcApiFeatures();

        return MigrationAcceptanceApplyStageRunner.BuildReport(
            "acceptance-apply-stage:slice-3",
            "acceptance-scan-stage:slice-2",
            [
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "arcgis-mapserver-mixed-renderers",
                    Inventory = arcgisInventory
                },
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "geoserver-mixed-catalog",
                    Inventory = geoServerInventory
                },
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = ogcInventory
                }
            ]);
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
