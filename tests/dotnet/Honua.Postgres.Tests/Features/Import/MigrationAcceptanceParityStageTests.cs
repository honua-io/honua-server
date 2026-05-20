// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Drives the migration acceptance suite parity stage end-to-end across the same fixture set used
/// by <see cref="MigrationAcceptanceScanStageTests"/> and <see cref="MigrationAcceptanceApplyStageTests"/>:
/// one ArcGIS GeoServices REST fixture, one GeoServer REST fixture, and one OGC API Features
/// snapshot fixture. The parity stage runs scan -> manifest -> apply -> parity probes (feature
/// count, schema field-names, axis-aligned bbox overlap) and asserts that the slice-4
/// <see cref="MigrationParityStageReport"/> emits the <see cref="MigrationParityEvidenceArtifact"/>
/// per fixture deterministically while routing manual-review resources through the manual-review
/// classification path.
/// </summary>
public sealed class MigrationAcceptanceParityStageTests
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public async Task ParityStage_AcrossArcGisGeoServerAndOgcApiFeatures_ProducesDeterministicReport()
    {
        var first = await BuildReportAsync();
        var second = await BuildReportAsync();

        first.RunId.Should().Be("acceptance-parity-stage:slice-4");
        first.ApplyRunId.Should().Be("acceptance-apply-stage:slice-3");
        first.ArtifactKind.Should().Be("honua.migration.parity-stage-report");
        first.ArtifactVersion.Should().Be("1.0");

        var firstJson = JsonSerializer.Serialize(first, ArtifactJsonOptions);
        var secondJson = JsonSerializer.Serialize(second, ArtifactJsonOptions);
        secondJson.Should().Be(firstJson, "the parity stage must be deterministic across re-runs of the same fixture set.");

        foreach (var entry in first.Sources)
        {
            entry.Outcome.ReplayToken.Should().StartWith("sha256:");
        }
    }

    [Fact]
    public async Task ParityStage_EmitsOneEvidenceArtifactPerFixtureSource()
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
            entry.Outcome.Evidence.Should().NotBeNull();
            entry.Outcome.Evidence.ArtifactKind.Should().Be("honua.migration.parity-evidence-pack");
            entry.Outcome.Evidence.ArtifactVersion.Should().Be("1.0");
            entry.Outcome.Evidence.SourceKind.Should().Be(entry.SourceKind);
            entry.Outcome.Evidence.ManifestAvailable.Should().BeTrue();
        }
    }

    [Fact]
    public async Task ParityStage_ChainsScanApplyParity_ForSingleFixture()
    {
        // A single test that drives scan -> apply -> parity for one fixture and asserts all three
        // artifacts emit deterministically — exercising the per-fixture path through the
        // acceptance suite.
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

        var parityReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:ogc-only",
            applyReport.RunId,
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = scanReport.Sources.Single().Inventory,
                    Manifest = applyReport.Sources.Single().Outcome.Manifest
                }
            ]);

        scanReport.Sources.Should().ContainSingle();
        applyReport.Sources.Should().ContainSingle();
        parityReport.Sources.Should().ContainSingle();
        parityReport.Sources.Single().FixtureId.Should().Be(scanReport.Sources.Single().FixtureId);
        parityReport.Sources.Single().SourceKind.Should().Be(applyReport.Sources.Single().SourceKind);
        parityReport.Sources.Single().Outcome.Evidence.SourceKind
            .Should().Be(scanReport.Sources.Single().SourceKind);

        var replayed = MigrationAcceptanceParityStageRunner.BuildReport(
            parityReport.RunId,
            parityReport.ApplyRunId,
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = scanReport.Sources.Single().Inventory,
                    Manifest = applyReport.Sources.Single().Outcome.Manifest
                }
            ]);

        var first = JsonSerializer.Serialize(parityReport, ArtifactJsonOptions);
        var second = JsonSerializer.Serialize(replayed, ArtifactJsonOptions);
        second.Should().Be(first, "scan -> apply -> parity for a single fixture must replay deterministically.");
    }

    [Fact]
    public async Task ParityStage_DeterministicReapply_MatchesReplayToken_PerSource()
    {
        var first = await BuildReportAsync();
        var second = await BuildReportAsync();

        first.Sources.Should().HaveSameCount(second.Sources);
        foreach (var (left, right) in first.Sources.Zip(second.Sources))
        {
            left.FixtureId.Should().Be(right.FixtureId);
            left.Outcome.ReplayToken.Should().Be(right.Outcome.ReplayToken,
                $"the parity stage replay token for fixture '{left.FixtureId}' must be stable across re-runs.");
        }
    }

    [Fact]
    public async Task ParityStage_RoutesManualReview_ForResourcesWithoutPublishableTarget()
    {
        // Drive the parity runner with a synthetic manifest whose target resource is routed to
        // manual-review so the per-resource classification path is exercised independently of
        // upstream scanner heuristics. This keeps the test asserting the runner contract rather
        // than a particular scanner's compatibility classifier.
        var inventory = ScanOgcApiFeatures();
        var manifest = MigrationManifestTranslator.Translate(inventory);
        var manualReviewManifest = manifest with
        {
            TargetResources = manifest.TargetResources
                .Select(resource => resource with { Action = "manual-review" })
                .ToArray()
        };

        var manualReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:manual-review",
            "acceptance-apply-stage:slice-3:manual-review",
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory,
                    Manifest = manualReviewManifest
                }
            ]);

        var entry = manualReport.Sources.Single();
        entry.Classification.Should().Be("manual-review",
            "a manifest that routes every target resource to manual review must roll up to the manual-review classification.");
        entry.Outcome.ManualReviewResourceCount.Should().BeGreaterThan(0);
        entry.Outcome.ResourceProbes
            .Should().OnlyContain(probe => probe.Classification == "manual-review");
        entry.Outcome.Diagnostics
            .Should().OnlyContain(diagnostic => diagnostic.Severity == "manual-review");

        // The full fixture-set report must still keep its summary counts coherent.
        var report = await BuildReportAsync();

        // Summary counts must match the per-source rollups exactly.
        report.Summary.ResourceCount.Should()
            .Be(report.Sources.Sum(entry => entry.Outcome.ResourceCount));
        report.Summary.FeatureCountMatchCount.Should()
            .Be(report.Sources.Sum(entry => entry.Outcome.FeatureCountMatchCount));
        report.Summary.SchemaMatchCount.Should()
            .Be(report.Sources.Sum(entry => entry.Outcome.SchemaMatchCount));
        report.Summary.DiagnosticCount.Should()
            .Be(report.Sources.Sum(entry => entry.Outcome.Diagnostics.Length));
        (report.Summary.PassSourceCount
            + report.Summary.WarnSourceCount
            + report.Summary.FailSourceCount
            + report.Summary.ManualReviewSourceCount).Should().Be(report.Summary.SourceCount);
    }

    [Fact]
    public async Task ParityStage_PassWarnFail_ThresholdsReachTheArtifact()
    {
        // Drive the parity runner directly with synthetic target observations so all three
        // classification thresholds (pass, warn, fail) are exercised on the per-resource probe and
        // surface to the aggregate fixture classification on the artifact.
        var inventory = await ScanArcGisAsync();
        var manifest = MigrationManifestTranslator.Translate(inventory);
        var publishableResources = manifest.TargetResources
            .Where(resource => resource.Action == "publish")
            .ToArray();
        publishableResources.Should().NotBeEmpty(
            "the ArcGIS MapServer-MixedRenderers fixture must expose at least one publishable resource for the threshold probe.");

        // Pass case: target observation exactly matches the source.
        var passInventory = inventory;
        var passManifest = manifest;
        var passReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:threshold-pass",
            "acceptance-apply-stage:slice-3:threshold",
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "arcgis-mapserver-mixed-renderers",
                    Inventory = passInventory,
                    Manifest = passManifest,
                    SourceObservations = publishableResources
                        .Select(resource => new MigrationParitySourceObservation
                        {
                            SourceResourceId = resource.SourceResourceId,
                            FeatureCount = 1000,
                            Bbox = [0, 0, 10, 10]
                        })
                        .ToArray(),
                    TargetObservations = publishableResources
                        .Select(resource => new MigrationParityTargetObservation
                        {
                            SourceResourceId = resource.SourceResourceId,
                            FeatureCount = 1000,
                            Bbox = [0, 0, 10, 10]
                        })
                        .ToArray()
                }
            ]);
        var passEntry = passReport.Sources.Single();
        passEntry.Outcome.ResourceProbes
            .Where(probe => probe.Classification != "manual-review")
            .Should().OnlyContain(probe => probe.Classification == "pass");

        // Warn case: target feature count differs by 1 — within the 1% warn threshold for 1000.
        var warnReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:threshold-warn",
            "acceptance-apply-stage:slice-3:threshold",
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "arcgis-mapserver-mixed-renderers",
                    Inventory = inventory,
                    Manifest = manifest,
                    SourceObservations = publishableResources
                        .Select(resource => new MigrationParitySourceObservation
                        {
                            SourceResourceId = resource.SourceResourceId,
                            FeatureCount = 1000
                        })
                        .ToArray(),
                    TargetObservations = publishableResources
                        .Select(resource => new MigrationParityTargetObservation
                        {
                            SourceResourceId = resource.SourceResourceId,
                            FeatureCount = 999
                        })
                        .ToArray()
                }
            ]);
        var warnEntry = warnReport.Sources.Single();
        warnEntry.Classification.Should().Be("warn",
            "a 1-feature drift on a 1000-feature resource must roll up to the warn classification.");
        warnEntry.Outcome.ResourceProbes
            .Where(probe => probe.Classification != "manual-review")
            .Should().Contain(probe => probe.FeatureCount.State == "warn");
        warnEntry.Outcome.Diagnostics
            .Should().Contain(diagnostic => diagnostic.Code == "parity.feature-count.warn");

        // Fail case: bbox is disjoint — must fail the bbox-overlap probe and roll up to fail.
        var failReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:threshold-fail",
            "acceptance-apply-stage:slice-3:threshold",
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "arcgis-mapserver-mixed-renderers",
                    Inventory = inventory,
                    Manifest = manifest,
                    SourceObservations = publishableResources
                        .Select(resource => new MigrationParitySourceObservation
                        {
                            SourceResourceId = resource.SourceResourceId,
                            FeatureCount = 1000,
                            Bbox = [0, 0, 10, 10]
                        })
                        .ToArray(),
                    TargetObservations = publishableResources
                        .Select(resource => new MigrationParityTargetObservation
                        {
                            SourceResourceId = resource.SourceResourceId,
                            FeatureCount = 1000,
                            Bbox = [100, 100, 110, 110]
                        })
                        .ToArray()
                }
            ]);
        var failEntry = failReport.Sources.Single();
        failEntry.Classification.Should().Be("fail",
            "a disjoint bbox observation must roll up to the fail classification.");
        failEntry.Outcome.BboxOverlapMismatchCount.Should().BeGreaterThan(0);
        failEntry.Outcome.Diagnostics
            .Should().Contain(diagnostic => diagnostic.Code == "parity.bbox-overlap.fail");
    }

    [Fact]
    public async Task ParityStage_RedactsCredentials_FromArtifactOutput()
    {
        var report = await BuildReportAsync();

        var json = JsonSerializer.Serialize(report, ArtifactJsonOptions);

        json.Should().NotContain("super-secret-token",
            "the parity stage evidence must never echo back the supplied ArcGIS token.");
        json.Should().NotContain("Authorization",
            "the parity stage evidence must not surface raw HTTP authorization headers.");
        json.Should().NotContain("password",
            "the parity stage evidence must not surface password values from the source.");
        json.Should().NotContain("Basic ",
            "the parity stage evidence must not surface raw Basic auth values from the source.");
    }

    private static async Task<MigrationParityStageReport> BuildReportAsync()
    {
        var arcgisInventory = await ScanArcGisAsync();
        var geoServerInventory = await ScanGeoServerAsync();
        var ogcInventory = ScanOgcApiFeatures();

        var applyReport = MigrationAcceptanceApplyStageRunner.BuildReport(
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

        var inventoryByFixture = new Dictionary<string, MigrationSourceInventoryArtifact>(StringComparer.Ordinal)
        {
            ["arcgis-mapserver-mixed-renderers"] = arcgisInventory,
            ["geoserver-mixed-catalog"] = geoServerInventory,
            ["ogc-api-features-demo"] = ogcInventory
        };

        return MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4",
            applyReport.RunId,
            applyReport.Sources.Select(entry => new MigrationAcceptanceParityStageInput
            {
                FixtureId = entry.FixtureId,
                Inventory = inventoryByFixture[entry.FixtureId],
                Manifest = entry.Outcome.Manifest
            }).ToArray());
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
