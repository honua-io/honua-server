// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
#pragma warning disable CA1305 // DateTimeOffset.Parse with string literal — fixture data is invariant, locale-sensitivity is not a concern for test inputs.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Slice 5 (capstone) of issue #1030: deterministic OGC coverage migration
/// evidence-pack generation tests. Covers fingerprint determinism, schema
/// stability, credential redaction, combined OGC API Coverages + WCS
/// roll-up, scoping evidence, and aggregated style-diagnostic surfacing.
/// </summary>
public sealed class OgcCoverageMigrationEvidencePackBuilderTests
{
    [Fact]
    public void Build_ProducesDeterministicFingerprint_ForIdenticalInputs()
    {
        var inputs = BuildCombinedInputs();

        var first = OgcCoverageMigrationEvidencePackBuilder.Build(inputs, new OgcCoverageMigrationEvidencePackBuilderOptions
        {
            RunId = "nightly-20260519",
            Generator = "test/1.0",
            GeneratedAt = DateTimeOffset.Parse("2026-05-19T00:00:00Z")
        });

        var second = OgcCoverageMigrationEvidencePackBuilder.Build(inputs, new OgcCoverageMigrationEvidencePackBuilderOptions
        {
            // Different run-time metadata; fingerprint must be unaffected.
            RunId = "nightly-20260601",
            Generator = "test/2.0",
            GeneratedAt = DateTimeOffset.Parse("2026-06-01T12:34:56Z")
        });

        first.BundleFingerprint.Should().StartWith("sha256:");
        first.BundleFingerprint.Should().Be(second.BundleFingerprint,
            "fingerprint must cover the bundle only — wall-clock, run id, and generator labels are excluded so nightly re-runs stay byte-identical.");

        first.RunId.Should().Be("nightly-20260519");
        second.RunId.Should().Be("nightly-20260601");
    }

    [Fact]
    public void Build_FingerprintChanges_WhenACoverageRecordChanges()
    {
        var inputs = BuildCombinedInputs();
        var mutated = inputs with
        {
            OgcApiCoveragesImport = inputs.OgcApiCoveragesImport! with
            {
                Records = inputs.OgcApiCoveragesImport!.Records
                    .Select(r => r.SourceCoverageId == "coverages/dem"
                        ? r with { Action = "manual-review", ErrorMessage = "Operator review required." }
                        : r)
                    .ToArray()
            }
        };

        var baseline = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);
        var changed = OgcCoverageMigrationEvidencePackBuilder.Build(mutated);

        baseline.BundleFingerprint.Should().NotBe(changed.BundleFingerprint,
            "any change to the bundle inputs must propagate to the fingerprint.");
    }

    [Fact]
    public void Build_FingerprintChanges_WhenAStyleDiagnosticChanges()
    {
        var inputs = BuildCombinedInputs();
        var mutated = inputs with
        {
            OgcApiCoveragesImport = inputs.OgcApiCoveragesImport! with
            {
                StyleDiagnostics =
                [
                    new MigrationCoverageStyleDiagnostic
                    {
                        Kind = "renderingHint",
                        Classification = "manual-review",
                        SourceCoverageId = "coverages/dem",
                        Reason = "Different reason than baseline."
                    }
                ]
            }
        };

        var baseline = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);
        var changed = OgcCoverageMigrationEvidencePackBuilder.Build(mutated);

        baseline.BundleFingerprint.Should().NotBe(changed.BundleFingerprint,
            "style-diagnostic changes must propagate to the fingerprint.");
    }

    [Fact]
    public void Build_GroupsChannelsInCanonicalOrder_RegardlessOfCallerOrder()
    {
        var inputs = BuildCombinedInputs();

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.Channels.Should().HaveCount(2);
        pack.Bundle.Channels.Select(c => c.Id).Should().Equal(
            OgcCoverageMigrationEvidencePackChannelIds.OgcApiCoverages,
            OgcCoverageMigrationEvidencePackChannelIds.Wcs);
    }

    [Fact]
    public void Build_EmitsOnlyOgcApiCoveragesChannel_WhenWcsResultMissing()
    {
        var inputs = BuildCombinedInputs() with { WcsImport = null };

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.Channels.Should().HaveCount(1);
        pack.Bundle.Channels[0].Id.Should().Be(OgcCoverageMigrationEvidencePackChannelIds.OgcApiCoverages);
    }

    [Fact]
    public void Build_EmitsOnlyWcsChannel_WhenOgcApiCoveragesResultMissing()
    {
        var inputs = BuildCombinedInputs() with { OgcApiCoveragesImport = null };

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.Channels.Should().HaveCount(1);
        pack.Bundle.Channels[0].Id.Should().Be(OgcCoverageMigrationEvidencePackChannelIds.Wcs);
        pack.Bundle.Channels[0].ResolvedVersion.Should().Be("2.0.1");
        pack.Bundle.Channels[0].RequestedOutputFormat.Should().Be("image/tiff");
    }

    [Fact]
    public void Build_Throws_WhenBothImportResultsMissing()
    {
        var inputs = BuildCombinedInputs() with
        {
            OgcApiCoveragesImport = null,
            WcsImport = null
        };

        var act = () => OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*OgcApiCoveragesImport*WcsImport*");
    }

    [Fact]
    public void Build_RollsUpSummary_AcrossBothChannels()
    {
        var inputs = BuildCombinedInputs();

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        // OGC API: 1 imported (dem) + 1 manual-review (orthomosaic) = 2 records.
        // WCS: 1 imported (climate-baseline) + 1 failed (legacy-radar) = 2 records.
        pack.Bundle.Summary.TotalCoverageCount.Should().Be(4);
        pack.Bundle.Summary.ImportedCount.Should().Be(2);
        pack.Bundle.Summary.ManualReviewCount.Should().Be(1);
        pack.Bundle.Summary.FailedCount.Should().Be(1);
        pack.Bundle.Summary.PlannedCount.Should().Be(0);
        pack.Bundle.Summary.SkippedCount.Should().Be(0);

        pack.Bundle.Summary.StyleDiagnosticCount.Should().BeGreaterThan(0);
        pack.Bundle.Summary.StyleManualReviewCount.Should().Be(
            pack.Bundle.StyleDiagnostics.Count(d => d.Classification == "manual-review"));
    }

    [Fact]
    public void Build_AggregatesStyleDiagnostics_AndDeduplicatesAcrossChannels()
    {
        var sharedDiagnostic = new MigrationCoverageStyleDiagnostic
        {
            Kind = "colorMap",
            Classification = "assisted",
            SourceCoverageId = "coverages/dem",
            Reason = "Indexed color table preserved verbatim.",
            SuggestedTargetStyleId = "indexed-color-table"
        };
        var ogcOnlyDiagnostic = new MigrationCoverageStyleDiagnostic
        {
            Kind = "renderingHint",
            Classification = "manual-review",
            SourceCoverageId = "coverages/orthomosaic",
            Reason = "Vendor-specific renderer requires manual review.",
            VendorName = "Esri"
        };

        var inputs = BuildCombinedInputs() with
        {
            OgcApiCoveragesImport = BuildCombinedInputs().OgcApiCoveragesImport! with
            {
                StyleDiagnostics = [sharedDiagnostic, ogcOnlyDiagnostic]
            },
            WcsImport = BuildCombinedInputs().WcsImport! with
            {
                // Same diagnostic surfaced through WCS channel; must dedupe.
                StyleDiagnostics = [sharedDiagnostic]
            }
        };

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.StyleDiagnostics.Should().HaveCount(2,
            "duplicate diagnostics across channels collapse to a single row in the pack.");
        pack.Bundle.StyleDiagnostics.Select(d => d.SourceCoverageId)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
        pack.Bundle.Summary.StyleDiagnosticCount.Should().Be(2);
        pack.Bundle.Summary.StyleManualReviewCount.Should().Be(1,
            "slice-4 manual-review style diagnostics must be surfaced so the pack does not claim visual parity for them.");
    }

    [Fact]
    public void Build_RedactsCredentials_FromSourceUrls()
    {
        var inputs = BuildCombinedInputs();
        const string SecretUrl = "https://admin:hunter2@coverage.example.com:8443/ogcapi?token=topsecret";

        var withSecretUrl = inputs with
        {
            Inventory = inputs.Inventory with
            {
                Source = inputs.Inventory.Source with { BaseUrl = SecretUrl }
            },
            OgcApiCoveragesImport = inputs.OgcApiCoveragesImport! with
            {
                Manifest = inputs.OgcApiCoveragesImport!.Manifest with
                {
                    Source = inputs.OgcApiCoveragesImport!.Manifest.Source with { BaseUrl = SecretUrl }
                }
            },
            WcsImport = inputs.WcsImport! with
            {
                Manifest = inputs.WcsImport!.Manifest with
                {
                    Source = inputs.WcsImport!.Manifest.Source with { BaseUrl = SecretUrl }
                }
            }
        };

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(withSecretUrl);

        pack.Bundle.Source.BaseUrl.Should().NotContain("hunter2");
        pack.Bundle.Source.BaseUrl.Should().NotContain("topsecret");
        pack.Bundle.Source.BaseUrl.Should().Be("https://coverage.example.com:8443/ogcapi");

        pack.Bundle.Inventory.Source.BaseUrl.Should().NotContain("hunter2");
        pack.Bundle.Inventory.Source.BaseUrl.Should().NotContain("topsecret");

        foreach (var channel in pack.Bundle.Channels)
        {
            channel.Manifest.Source.BaseUrl.Should().NotContain("hunter2");
            channel.Manifest.Source.BaseUrl.Should().NotContain("topsecret");
        }

        // Serialize the entire pack and assert no credential leaked anywhere.
        var json = JsonSerializer.Serialize(
            pack,
            OgcCoverageMigrationEvidencePackJsonContext.Default.OgcCoverageMigrationEvidencePackArtifact);
        json.Should().NotContain("hunter2");
        json.Should().NotContain("topsecret");
        json.Should().NotContain("admin:hunter2");
    }

    [Fact]
    public void Build_CapturesCoverageScope_WhenOperatorRestrictsRun()
    {
        var inputs = BuildCombinedInputs() with
        {
            RequestedCoverageIds = new[] { "coverages/dem", "coverages/dem", "coverages/orthomosaic" }
        };

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.CoverageScope.Restricted.Should().BeTrue();
        pack.Bundle.CoverageScope.CoverageIds.Should().Equal("coverages/dem", "coverages/orthomosaic");
    }

    [Fact]
    public void Build_CapturesCoverageScope_AsUnrestricted_WhenNoIdsProvided()
    {
        var inputs = BuildCombinedInputs();

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.CoverageScope.Restricted.Should().BeFalse();
        pack.Bundle.CoverageScope.CoverageIds.Should().BeEmpty();
    }

    [Fact]
    public void Build_OrdersRecordsBySourceCoverageId_WithinEachChannel()
    {
        var inputs = BuildCombinedInputs() with
        {
            OgcApiCoveragesImport = BuildCombinedInputs().OgcApiCoveragesImport! with
            {
                // Pass records in reverse order; builder must canonicalize.
                Records = BuildCombinedInputs().OgcApiCoveragesImport!.Records.Reverse().ToArray()
            }
        };

        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(inputs);

        var ogcChannel = pack.Bundle.Channels
            .Single(c => c.Id == OgcCoverageMigrationEvidencePackChannelIds.OgcApiCoverages);
        ogcChannel.Records.Select(r => r.SourceCoverageId)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Artifact_Shape_HasStableTopLevelFields()
    {
        // Schema-stability guard: surface any accidental addition/rename of
        // the evidence-pack contract so reviewers update downstream
        // consumers (admin UI, SDK orchestration) intentionally.
        var pack = OgcCoverageMigrationEvidencePackBuilder.Build(BuildCombinedInputs());
        var json = JsonSerializer.Serialize(
            pack,
            OgcCoverageMigrationEvidencePackJsonContext.Default.OgcCoverageMigrationEvidencePackArtifact);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Top-level artifact contract.
        root.GetProperty("artifactKind").GetString().Should().Be("honua.migration.ogc-coverage-evidence-pack");
        root.GetProperty("artifactVersion").GetString().Should().Be("1.0");
        root.GetProperty("runId").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("generator").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("generatedAt").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("bundleFingerprint").GetString().Should().StartWith("sha256:");

        // Bundle contract.
        var bundle = root.GetProperty("bundle");
        bundle.GetProperty("sourceKind").ValueKind.Should().Be(JsonValueKind.String);
        bundle.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("coverageScope").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("summary").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("channels").ValueKind.Should().Be(JsonValueKind.Array);
        bundle.GetProperty("styleDiagnostics").ValueKind.Should().Be(JsonValueKind.Array);
        bundle.GetProperty("inventory").ValueKind.Should().Be(JsonValueKind.Object);

        var summary = bundle.GetProperty("summary");
        summary.GetProperty("totalCoverageCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("importedCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("plannedCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("skippedCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("manualReviewCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("failedCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("styleDiagnosticCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("styleManualReviewCount").ValueKind.Should().Be(JsonValueKind.Number);

        var channels = bundle.GetProperty("channels");
        channels.GetArrayLength().Should().Be(2);
        foreach (var channel in channels.EnumerateArray())
        {
            channel.GetProperty("id").ValueKind.Should().Be(JsonValueKind.String);
            channel.GetProperty("applyMode").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
            channel.GetProperty("dryRun").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
            channel.GetProperty("coverageCount").ValueKind.Should().Be(JsonValueKind.Number);
            channel.GetProperty("records").ValueKind.Should().Be(JsonValueKind.Array);
            channel.GetProperty("manifest").ValueKind.Should().Be(JsonValueKind.Object);
        }
    }

    private static OgcCoverageMigrationEvidencePackInputs BuildCombinedInputs()
    {
        var source = new MigrationSourceIdentity
        {
            DisplayName = "Coverage Sample",
            BaseUrl = "https://coverage.example.com/ogcapi",
            Product = "OGC API Coverages",
            Version = "1.0",
            ServiceType = "REST"
        };

        var inventory = new MigrationSourceInventoryArtifact
        {
            SourceKind = "ogc-api-coverages",
            Source = source,
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "anonymous",
                CredentialsSupplied = false,
                AccessConfirmed = true
            },
            ScanCompleteness = new MigrationInventoryCompleteness { Status = "complete" },
            Summary = new MigrationInventorySummary
            {
                ContainerCount = 1,
                ResourceCount = 2,
                StyleCount = 0
            },
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Coverage inventory is compatible with Honua migration."
            }
        };

        var manifest = new MigrationManifestArtifact
        {
            SourceKind = "ogc-api-coverages",
            Source = source,
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = 2,
                TargetResourceCount = 2,
                StyleActionCount = 0
            }
        };

        var ogcCoveragesResult = new OgcCoverageImportResult
        {
            Manifest = manifest,
            ApplyMode = true,
            DryRun = false,
            Records =
            [
                new OgcCoverageImportRecord
                {
                    SourceCoverageId = "coverages/orthomosaic",
                    TargetCoverageName = "ortho-2026",
                    OutputFormat = "CloudOptimizedGeoTIFF",
                    Classification = "manual-review",
                    Action = "manual-review",
                    DeclaredCrs = ["EPSG:3857"],
                    DeclaredBands = ["red", "green", "blue"]
                },
                new OgcCoverageImportRecord
                {
                    SourceCoverageId = "coverages/dem",
                    TargetCoverageName = "dem-2026",
                    OutputFormat = "GeoTIFF",
                    Classification = "automated",
                    Action = "imported",
                    DeclaredCrs = ["EPSG:4326"],
                    DeclaredBands = ["elevation"],
                    ByteCount = 1024 * 1024,
                    RasterId = 42L
                }
            ],
            StyleDiagnostics =
            [
                new MigrationCoverageStyleDiagnostic
                {
                    Kind = "colorMap",
                    Classification = "assisted",
                    SourceCoverageId = "coverages/dem",
                    Reason = "Indexed color table preserved verbatim.",
                    SuggestedTargetStyleId = "indexed-color-table"
                },
                new MigrationCoverageStyleDiagnostic
                {
                    Kind = "renderingHint",
                    Classification = "manual-review",
                    SourceCoverageId = "coverages/orthomosaic",
                    Reason = "Vendor-specific renderer requires manual review.",
                    VendorName = "Esri"
                }
            ]
        };

        var wcsResult = new OgcWcsImportResult
        {
            Manifest = manifest with { SourceKind = "ogc-wcs" },
            ApplyMode = true,
            DryRun = false,
            RequestedOutputFormat = "image/tiff",
            ResolvedVersion = "2.0.1",
            Records =
            [
                new OgcCoverageImportRecord
                {
                    SourceCoverageId = "wcs:legacy-radar",
                    TargetCoverageName = "legacy-radar-2026",
                    OutputFormat = "GeoTIFF",
                    Classification = "manual-review",
                    Action = "failed",
                    DeclaredCrs = ["EPSG:4326"],
                    DeclaredBands = ["dbz"],
                    ErrorMessage = "Source service returned a malformed envelope."
                },
                new OgcCoverageImportRecord
                {
                    SourceCoverageId = "wcs:climate-baseline",
                    TargetCoverageName = "climate-baseline-2026",
                    OutputFormat = "GeoTIFF",
                    Classification = "automated",
                    Action = "imported",
                    DeclaredCrs = ["EPSG:4326"],
                    DeclaredBands = ["temperature"],
                    ByteCount = 2L * 1024 * 1024,
                    RasterId = 43L
                }
            ],
            StyleDiagnostics =
            [
                new MigrationCoverageStyleDiagnostic
                {
                    Kind = "noDataValue",
                    Classification = "automated",
                    SourceCoverageId = "wcs:legacy-radar",
                    Reason = "NoData value preserved by raw pixel copy."
                }
            ]
        };

        return new OgcCoverageMigrationEvidencePackInputs
        {
            Inventory = inventory,
            OgcApiCoveragesImport = ogcCoveragesResult,
            WcsImport = wcsResult
        };
    }
}
