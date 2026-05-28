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
/// Drives the migration acceptance suite readiness stage end-to-end across the same fixture set
/// used by the slice-2 scan, slice-3 apply, and slice-4 parity stage tests: one ArcGIS GeoServices
/// REST fixture, one GeoServer REST fixture, and one OGC API Features snapshot fixture. The
/// readiness stage consumes the upstream scan, apply, and parity reports and emits one
/// <see cref="MigrationReadinessAttestationArtifact"/> per source plus the aggregate
/// <see cref="MigrationReadinessStageReport"/>, classifying each source as <c>ready</c>,
/// <c>conditional</c>, or <c>not-ready</c> with cited evidence artifact hashes.
/// </summary>
public sealed class MigrationAcceptanceReadinessStageTests
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public async Task ReadinessStage_AcrossArcGisGeoServerAndOgcApiFeatures_ProducesDeterministicReport()
    {
        var first = await BuildReportAsync();
        var second = await BuildReportAsync();

        first.RunId.Should().Be("acceptance-readiness-stage:slice-5");
        first.ScanRunId.Should().Be("acceptance-scan-stage:slice-2");
        first.ApplyRunId.Should().Be("acceptance-apply-stage:slice-3");
        first.ParityRunId.Should().Be("acceptance-parity-stage:slice-4");
        first.ArtifactKind.Should().Be("honua.migration.readiness-stage-report");
        first.ArtifactVersion.Should().Be("1.0");

        var firstJson = JsonSerializer.Serialize(first, ArtifactJsonOptions);
        var secondJson = JsonSerializer.Serialize(second, ArtifactJsonOptions);
        secondJson.Should().Be(firstJson, "the readiness stage must be deterministic across re-runs of the same fixture set.");

        foreach (var entry in first.Sources)
        {
            entry.Attestation.ReplayToken.Should().StartWith("sha256:");
            entry.Attestation.EvidenceCitations
                .Should().OnlyContain(citation => citation.ArtifactHash.StartsWith("sha256:"));
        }
    }

    [Fact]
    public async Task ReadinessStage_EmitsOneAttestationPerFixtureSource()
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
            entry.Attestation.Should().NotBeNull();
            entry.Attestation.ArtifactKind.Should().Be("honua.migration.readiness-attestation");
            entry.Attestation.ArtifactVersion.Should().Be("1.0");
            entry.Attestation.FixtureId.Should().Be(entry.FixtureId);
            entry.Attestation.SourceKind.Should().Be(entry.SourceKind);
            entry.Attestation.Status.Should().Be(entry.Status);
            entry.Attestation.EvidenceCitations.Select(citation => citation.Stage)
                .Should().Equal("apply", "parity", "scan");
        }

        // Summary counts must match the per-source rollups exactly.
        (report.Summary.ReadySourceCount
            + report.Summary.ConditionalSourceCount
            + report.Summary.NotReadySourceCount).Should().Be(report.Summary.SourceCount);
        report.Summary.ReasonCount.Should()
            .Be(report.Sources.Sum(entry => entry.Attestation.Reasons.Length));
        report.Summary.EvidenceCitationCount.Should()
            .Be(report.Sources.Sum(entry => entry.Attestation.EvidenceCitations.Length));
    }

    [Fact]
    public void ReadinessStage_ChainsScanApplyParityReadiness_ForSingleFixture()
    {
        // A single test that drives scan -> apply -> parity -> readiness for one fixture and
        // asserts all four artifacts emit deterministically — exercising the per-fixture path
        // through the full acceptance suite.
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

        var readinessReport = MigrationAcceptanceReadinessStageRunner.BuildReport(
            "acceptance-readiness-stage:slice-5:ogc-only",
            scanReport,
            applyReport,
            parityReport);

        readinessReport.Sources.Should().ContainSingle();
        var entry = readinessReport.Sources.Single();
        entry.FixtureId.Should().Be(scanReport.Sources.Single().FixtureId);
        entry.SourceKind.Should().Be(parityReport.Sources.Single().SourceKind);
        entry.Status.Should().BeOneOf("ready", "conditional", "not-ready");
        entry.Attestation.EvidenceCitations.Should().HaveCount(3);

        var replayed = MigrationAcceptanceReadinessStageRunner.BuildReport(
            readinessReport.RunId,
            scanReport,
            applyReport,
            parityReport);

        var first = JsonSerializer.Serialize(readinessReport, ArtifactJsonOptions);
        var second = JsonSerializer.Serialize(replayed, ArtifactJsonOptions);
        second.Should().Be(first, "scan -> apply -> parity -> readiness for a single fixture must replay deterministically.");
    }

    [Fact]
    public async Task ReadinessStage_DeterministicReapply_MatchesReplayToken_PerSource()
    {
        var first = await BuildReportAsync();
        var second = await BuildReportAsync();

        first.Sources.Should().HaveSameCount(second.Sources);
        foreach (var (left, right) in first.Sources.Zip(second.Sources))
        {
            left.FixtureId.Should().Be(right.FixtureId);
            left.Attestation.ReplayToken.Should().Be(right.Attestation.ReplayToken,
                $"the readiness stage replay token for fixture '{left.FixtureId}' must be stable across re-runs.");
            left.Attestation.EvidenceCitations.Should().HaveSameCount(right.Attestation.EvidenceCitations);
            foreach (var (leftCitation, rightCitation) in left.Attestation.EvidenceCitations.Zip(right.Attestation.EvidenceCitations))
            {
                leftCitation.ArtifactHash.Should().Be(rightCitation.ArtifactHash,
                    $"the cited evidence hash for stage '{leftCitation.Stage}' must be stable across re-runs.");
            }
        }
    }

    [Fact]
    public void ReadinessStage_RoutesConditional_WhenOnlyManualReviewReasons()
    {
        // Drive the readiness runner with a synthetic manifest whose target resources are routed to
        // manual-review so the parity stage rolls up to manual-review with no fails. The readiness
        // stage must then classify the source as conditional (not ready, not failing).
        var inventory = ScanOgcApiFeatures();
        var manifest = MigrationManifestTranslator.Translate(inventory);
        var manualReviewManifest = manifest with
        {
            TargetResources = manifest.TargetResources
                .Select(resource => resource with { Action = "manual-review" })
                .ToArray()
        };

        var scanReport = MigrationAcceptanceScanStageRunner.BuildReport(
            "acceptance-scan-stage:slice-2:manual-review",
            [
                new MigrationAcceptanceScanStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory
                }
            ]);
        var applyReport = MigrationAcceptanceApplyStageRunner.BuildReport(
            "acceptance-apply-stage:slice-3:manual-review",
            scanReport.RunId,
            [
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory,
                    Manifest = manualReviewManifest
                }
            ]);
        var parityReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:manual-review",
            applyReport.RunId,
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory,
                    Manifest = manualReviewManifest
                }
            ]);

        var readinessReport = MigrationAcceptanceReadinessStageRunner.BuildReport(
            "acceptance-readiness-stage:slice-5:manual-review",
            scanReport,
            applyReport,
            parityReport);

        var entry = readinessReport.Sources.Single();
        entry.Status.Should().Be("conditional",
            "a parity stage rolled up to manual-review with no fails must surface as conditional readiness.");
        entry.Attestation.Reasons
            .Should().Contain(reason => reason.Severity == "manual-review",
                "manual-review parity diagnostics must produce manual-review readiness reasons.");
        entry.Attestation.Reasons
            .Should().NotContain(reason => reason.Severity == "fail",
                "manual-review-only inputs must not surface fail reasons.");
        readinessReport.Summary.ConditionalSourceCount.Should().Be(1);
        readinessReport.Summary.NotReadySourceCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadinessStage_RoutesNotReady_WhenParityFails()
    {
        // Drive the readiness stage with a parity report whose probe fails so the readiness stage
        // must classify the source as not-ready and surface a fail reason.
        var inventory = await ScanArcGisAsync();
        var manifest = MigrationManifestTranslator.Translate(inventory);
        var publishableResources = manifest.TargetResources
            .Where(resource => resource.Action == "publish")
            .ToArray();
        publishableResources.Should().NotBeEmpty(
            "the ArcGIS fixture must expose at least one publishable resource for the fail-path test.");

        var scanReport = MigrationAcceptanceScanStageRunner.BuildReport(
            "acceptance-scan-stage:slice-2:fail",
            [
                new MigrationAcceptanceScanStageInput
                {
                    FixtureId = "arcgis-mapserver-mixed-renderers",
                    Inventory = inventory
                }
            ]);
        var applyReport = MigrationAcceptanceApplyStageRunner.BuildReport(
            "acceptance-apply-stage:slice-3:fail",
            scanReport.RunId,
            [
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "arcgis-mapserver-mixed-renderers",
                    Inventory = inventory
                }
            ]);
        var parityReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:fail",
            applyReport.RunId,
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

        var readinessReport = MigrationAcceptanceReadinessStageRunner.BuildReport(
            "acceptance-readiness-stage:slice-5:fail",
            scanReport,
            applyReport,
            parityReport);

        var entry = readinessReport.Sources.Single();
        entry.Status.Should().Be("not-ready",
            "a parity failure must surface as not-ready readiness.");
        entry.Attestation.Reasons
            .Should().Contain(reason => reason.Severity == "fail",
                "parity fail diagnostics must produce fail readiness reasons.");
        readinessReport.Summary.NotReadySourceCount.Should().Be(1);
        readinessReport.Summary.ReadySourceCount.Should().Be(0);
    }

    [Fact]
    public void ReadinessStage_RoutesReady_WhenAllProbesPassAndNoManualReview()
    {
        // Drive the readiness stage with a clean OGC API Features fixture whose parity stage passes
        // and whose apply stage has no manual-review items. The readiness stage must classify the
        // source as ready.
        var inventory = ScanOgcApiFeatures();

        var scanReport = MigrationAcceptanceScanStageRunner.BuildReport(
            "acceptance-scan-stage:slice-2:ready",
            [
                new MigrationAcceptanceScanStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory
                }
            ]);
        var applyReport = MigrationAcceptanceApplyStageRunner.BuildReport(
            "acceptance-apply-stage:slice-3:ready",
            scanReport.RunId,
            [
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory
                }
            ]);
        var parityReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:ready",
            applyReport.RunId,
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory,
                    Manifest = applyReport.Sources.Single().Outcome.Manifest
                }
            ]);

        // Only assert ready if the upstream parity stage actually passed without manual review for
        // this fixture; otherwise this becomes a contract assertion on the fixture rather than the
        // runner. We assert ready when the parity report classifies pass and apply has no manual
        // review; otherwise conditional. Either way the readiness stage must not return not-ready.
        var parityEntry = parityReport.Sources.Single();
        var applyEntry = applyReport.Sources.Single();
        var expectedStatus = parityEntry.Classification == "pass"
            && applyEntry.Outcome.ManualReviewItemCount == 0
            && applyEntry.Outcome.UnsupportedItemCount == 0
            && parityEntry.Outcome.ManualReviewResourceCount == 0
                ? "ready"
                : "conditional";

        var readinessReport = MigrationAcceptanceReadinessStageRunner.BuildReport(
            "acceptance-readiness-stage:slice-5:ready",
            scanReport,
            applyReport,
            parityReport);

        var entry = readinessReport.Sources.Single();
        entry.Status.Should().Be(expectedStatus);
        entry.Status.Should().NotBe("not-ready",
            "a fixture with no parity failures and no unsupported items must never surface as not-ready.");
        if (expectedStatus == "ready")
        {
            entry.Attestation.Reasons.Should().BeEmpty(
                "a ready attestation must not carry blocking reasons.");
        }
    }

    [Fact]
    public async Task ReadinessStage_CitesScanApplyParityEvidence_WithDeterministicHashes()
    {
        var report = await BuildReportAsync();

        foreach (var entry in report.Sources)
        {
            entry.Attestation.EvidenceCitations.Should().HaveCount(3);
            entry.Attestation.EvidenceCitations
                .Should().Contain(citation => citation.Stage == "scan"
                    && citation.ArtifactKind == "honua.migration.source-inventory");
            entry.Attestation.EvidenceCitations
                .Should().Contain(citation => citation.Stage == "apply"
                    && citation.ArtifactKind == "honua.migration.manifest");
            entry.Attestation.EvidenceCitations
                .Should().Contain(citation => citation.Stage == "parity"
                    && citation.ArtifactKind == "honua.migration.parity-evidence-pack"
                    && citation.ReplayToken!.StartsWith("sha256:"));

            foreach (var citation in entry.Attestation.EvidenceCitations)
            {
                citation.ArtifactHash.Should().MatchRegex("^sha256:[0-9a-f]{64}$",
                    "every cited evidence hash must be a lower-case SHA-256 fingerprint.");
            }
        }
    }

    [Fact]
    public async Task ReadinessStage_RedactsCredentials_FromAttestationOutput()
    {
        var report = await BuildReportAsync();

        var json = JsonSerializer.Serialize(report, ArtifactJsonOptions);

        json.Should().NotContain("super-secret-token",
            "the readiness attestation must never echo back the supplied ArcGIS token.");
        json.Should().NotContain("Authorization",
            "the readiness attestation must not surface raw HTTP authorization headers.");
        json.Should().NotContain("password",
            "the readiness attestation must not surface password values from the source.");
        json.Should().NotContain("Basic ",
            "the readiness attestation must not surface raw Basic auth values from the source.");
    }

    [Fact]
    public void ReadinessStage_RejectsMissingScanOrApplyEntries_ForParitySource()
    {
        // The runner must refuse to produce an attestation when a parity entry has no matching
        // scan or apply entry — otherwise the cited evidence hashes would be incoherent.
        var inventory = ScanOgcApiFeatures();
        var scanReport = MigrationAcceptanceScanStageRunner.BuildReport(
            "acceptance-scan-stage:slice-2:mismatch",
            [
                new MigrationAcceptanceScanStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory
                }
            ]);
        var applyReport = MigrationAcceptanceApplyStageRunner.BuildReport(
            "acceptance-apply-stage:slice-3:mismatch",
            scanReport.RunId,
            [
                new MigrationAcceptanceApplyStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory
                }
            ]);
        var parityReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4:mismatch",
            applyReport.RunId,
            [
                new MigrationAcceptanceParityStageInput
                {
                    FixtureId = "ogc-api-features-demo",
                    Inventory = inventory,
                    Manifest = applyReport.Sources.Single().Outcome.Manifest
                }
            ]);

        // Mutate the parity report to refer to a fixture that the scan/apply reports do not know
        // about so the runner exercises its alignment validation.
        var orphanParity = parityReport with
        {
            Sources = parityReport.Sources
                .Select(entry => entry with { FixtureId = "orphan-fixture" })
                .ToArray()
        };

        var act = () => MigrationAcceptanceReadinessStageRunner.BuildReport(
            "acceptance-readiness-stage:slice-5:mismatch",
            scanReport,
            applyReport,
            orphanParity);

        act.Should().Throw<ArgumentException>(
            "the readiness stage must refuse to attest sources that lack a matching scan entry.");
    }

    private static async Task<MigrationReadinessStageReport> BuildReportAsync()
    {
        var arcgisInventory = await ScanArcGisAsync();
        var geoServerInventory = await ScanGeoServerAsync();
        var ogcInventory = ScanOgcApiFeatures();

        var scanReport = MigrationAcceptanceScanStageRunner.BuildReport(
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

        var applyReport = MigrationAcceptanceApplyStageRunner.BuildReport(
            "acceptance-apply-stage:slice-3",
            scanReport.RunId,
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

        var parityReport = MigrationAcceptanceParityStageRunner.BuildReport(
            "acceptance-parity-stage:slice-4",
            applyReport.RunId,
            applyReport.Sources.Select(entry => new MigrationAcceptanceParityStageInput
            {
                FixtureId = entry.FixtureId,
                Inventory = inventoryByFixture[entry.FixtureId],
                Manifest = entry.Outcome.Manifest
            }).ToArray());

        return MigrationAcceptanceReadinessStageRunner.BuildReport(
            "acceptance-readiness-stage:slice-5",
            scanReport,
            applyReport,
            parityReport);
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
