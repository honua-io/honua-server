// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
#pragma warning disable CA1305 // DateTimeOffset.Parse with string literal — fixture data is invariant, locale-sensitivity is not a concern for test inputs.

using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Slice 5 (capstone) of issue #1029. Verifies the OGC API Features migration evidence
/// pack builder is deterministic, fingerprint-sensitive to bundle changes, schema-stable
/// at the top level, and strips source URLs / credentials before serialization.
/// </summary>
public sealed class OgcApiFeaturesMigrationEvidencePackBuilderTests
{
    [Fact]
    public void Build_ProducesDeterministicFingerprint_ForIdenticalInputs()
    {
        var inputs = BuildInputs();

        var first = OgcApiFeaturesMigrationEvidencePackBuilder.Build(
            inputs,
            new OgcApiFeaturesMigrationEvidencePackBuilderOptions
            {
                RunId = "nightly-20260519",
                Generator = "test/1.0",
                GeneratedAt = DateTimeOffset.Parse("2026-05-19T00:00:00Z")
            });

        var second = OgcApiFeaturesMigrationEvidencePackBuilder.Build(
            inputs,
            new OgcApiFeaturesMigrationEvidencePackBuilderOptions
            {
                // Different run-time metadata; fingerprint must be unaffected.
                RunId = "nightly-20260601",
                Generator = "test/2.0",
                GeneratedAt = DateTimeOffset.Parse("2026-06-01T12:34:56Z")
            });

        first.BundleFingerprint.Should().StartWith("sha256:");
        first.BundleFingerprint.Should().Be(second.BundleFingerprint,
            "fingerprint must cover the bundle only — wall-clock and generator labels are excluded so nightly re-runs stay byte-identical.");

        first.RunId.Should().Be("nightly-20260519");
        second.RunId.Should().Be("nightly-20260601");
    }

    [Fact]
    public void Build_ProducesStableCollectionOrder_RegardlessOfInputOrder()
    {
        var inputs = BuildInputs();
        var reversed = inputs with
        {
            CollectionResults = inputs.CollectionResults!.Reverse().ToArray()
        };

        var canonical = OgcApiFeaturesMigrationEvidencePackBuilder.Build(inputs);
        var shuffled = OgcApiFeaturesMigrationEvidencePackBuilder.Build(reversed);

        canonical.BundleFingerprint.Should().Be(shuffled.BundleFingerprint,
            "collection ordering must be normalized by the builder so callers can supply results in any order.");
        canonical.Bundle.Collections.Select(static c => c.CollectionId)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Build_FingerprintChanges_WhenACollectionResultChanges()
    {
        var inputs = BuildInputs();
        var mutated = inputs with
        {
            CollectionResults = inputs.CollectionResults!
                .Select(input => input.Result.CollectionId == "roads"
                    ? input with
                    {
                        Result = input.Result with { FeaturesImported = input.Result.FeaturesImported + 1 }
                    }
                    : input)
                .ToArray()
        };

        var baseline = OgcApiFeaturesMigrationEvidencePackBuilder.Build(inputs);
        var changed = OgcApiFeaturesMigrationEvidencePackBuilder.Build(mutated);

        baseline.BundleFingerprint.Should().NotBe(changed.BundleFingerprint,
            "any change to the bundle inputs must propagate to the fingerprint.");
    }

    [Fact]
    public void Build_FingerprintChanges_WhenFilterScopePushdownChanges()
    {
        var inputs = BuildInputs();
        var mutated = inputs with
        {
            CollectionResults = inputs.CollectionResults!
                .Select(input => input.Result.CollectionId == "roads"
                    ? input with { Filter = "highway = 'primary'" }
                    : input)
                .ToArray()
        };

        var baseline = OgcApiFeaturesMigrationEvidencePackBuilder.Build(inputs);
        var changed = OgcApiFeaturesMigrationEvidencePackBuilder.Build(mutated);

        baseline.BundleFingerprint.Should().NotBe(changed.BundleFingerprint,
            "slice-3 filter pushdown participates in the fingerprint so callers can detect scope drift.");
    }

    [Fact]
    public void Build_AggregatesSummary_AcrossCollectionResults()
    {
        var inputs = BuildInputs();

        var pack = OgcApiFeaturesMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.Summary.CollectionResultCount.Should().Be(3);
        pack.Bundle.Summary.SucceededCollectionCount.Should().Be(2);
        pack.Bundle.Summary.FailedCollectionCount.Should().Be(1);
        pack.Bundle.Summary.TotalFeaturesImported.Should().Be(125);
        pack.Bundle.Summary.TotalFeaturesSkipped.Should().Be(2);
        pack.Bundle.Summary.TotalPagesFetched.Should().Be(5);
        pack.Bundle.Summary.TruncatedCollectionCount.Should().Be(1);
        pack.Bundle.Summary.ScopeDriftCollectionCount.Should().Be(1);
        pack.Bundle.Summary.TotalSchemaMappingDiagnosticCount.Should().Be(2);
        pack.Bundle.Summary.SchemaMappingManualReviewCount.Should().Be(1);
        pack.Bundle.Summary.SchemaMappingUnsupportedCount.Should().Be(1);
        pack.Bundle.Summary.InventoryCollectionCount.Should().Be(3,
            "summary mirrors the inventory snapshot's advertised collection count.");
        pack.Bundle.Summary.ConformanceClassCount.Should().Be(2,
            "conformance classes are recorded as ogc-api-features-conformance external dependencies by slice 1.");
    }

    [Fact]
    public void Build_SurfacesFilterScopeAndManualReviewReason()
    {
        var pack = OgcApiFeaturesMigrationEvidencePackBuilder.Build(BuildInputs());

        var drifted = pack.Bundle.Collections.Single(c => c.CollectionId == "rivers");
        drifted.FilterScope.Filter.Should().Be("name LIKE 'Sandy%'");
        drifted.FilterScope.Bbox.Should().Be("-180,-90,180,90");
        drifted.FilterScope.Datetime.Should().Be("2026-01-01T00:00:00Z/..");
        drifted.FilterScope.ScopeDriftDetected.Should().BeTrue();
        drifted.FilterScope.ManualReviewReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Build_RedactsCredentials_FromSourceUrlAndArtifact()
    {
        var inputs = BuildInputs();
        var withSecretUrl = inputs with
        {
            Inventory = inputs.Inventory with
            {
                Source = inputs.Inventory.Source with
                {
                    BaseUrl = "https://admin:hunter2@ogc.example.com:8443/ogcapi?token=topsecret"
                }
            }
        };

        var pack = OgcApiFeaturesMigrationEvidencePackBuilder.Build(withSecretUrl);

        pack.Bundle.Source.BaseUrl.Should().NotContain("hunter2");
        pack.Bundle.Source.BaseUrl.Should().NotContain("admin");
        pack.Bundle.Source.BaseUrl.Should().NotContain("topsecret");
        pack.Bundle.Source.BaseUrl.Should().Be("https://ogc.example.com:8443/ogcapi");
        pack.Bundle.Inventory.Source.BaseUrl.Should().Be("https://ogc.example.com:8443/ogcapi");

        var json = JsonSerializer.Serialize(
            pack,
            OgcApiFeaturesMigrationEvidencePackJsonContext.Default.OgcApiFeaturesMigrationEvidencePackArtifact);
        json.Should().NotContain("hunter2");
        json.Should().NotContain("topsecret");
        json.Should().NotContain("admin:hunter2");
        json.Should().NotContain("?token");
    }

    [Fact]
    public void Build_WithEmptyCollectionResults_EmitsInventoryOnlyPack()
    {
        var inputs = BuildInputs() with { CollectionResults = null };

        var pack = OgcApiFeaturesMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.Collections.Should().BeEmpty();
        pack.Bundle.Summary.CollectionResultCount.Should().Be(0);
        pack.Bundle.Summary.SucceededCollectionCount.Should().Be(0);
        pack.Bundle.Summary.FailedCollectionCount.Should().Be(0);
        pack.Bundle.Summary.TotalSchemaMappingDiagnosticCount.Should().Be(0);
        pack.BundleFingerprint.Should().StartWith("sha256:");
    }

    [Fact]
    public void Artifact_Shape_HasStableTopLevelFields()
    {
        // Schema-stability guard: surface any accidental addition/rename of the evidence
        // pack contract so reviewers update consumers (admin UI, nightly workflow) before
        // shipping.
        var pack = OgcApiFeaturesMigrationEvidencePackBuilder.Build(BuildInputs());
        var json = JsonSerializer.Serialize(
            pack,
            OgcApiFeaturesMigrationEvidencePackJsonContext.Default.OgcApiFeaturesMigrationEvidencePackArtifact);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Top-level artifact contract.
        root.GetProperty("artifactKind").GetString().Should().Be("honua.migration.ogc-api-features.evidence-pack");
        root.GetProperty("artifactVersion").GetString().Should().Be("1.0");
        root.GetProperty("runId").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("generator").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("generatedAt").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("bundleFingerprint").GetString().Should().StartWith("sha256:");

        // Bundle contract: inventory + per-collection imports + summary.
        var bundle = root.GetProperty("bundle");
        bundle.GetProperty("sourceKind").GetString().Should().Be("ogc-api-features");
        bundle.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("summary").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("inventory").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("collections").ValueKind.Should().Be(JsonValueKind.Array);

        var summary = bundle.GetProperty("summary");
        summary.GetProperty("inventoryCollectionCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("conformanceClassCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("collectionResultCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("succeededCollectionCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("failedCollectionCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("totalFeaturesImported").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("totalFeaturesSkipped").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("totalPagesFetched").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("truncatedCollectionCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("scopeDriftCollectionCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("totalSchemaMappingDiagnosticCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("schemaMappingManualReviewCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("schemaMappingUnsupportedCount").ValueKind.Should().Be(JsonValueKind.Number);

        var firstCollection = bundle.GetProperty("collections")[0];
        firstCollection.GetProperty("collectionId").ValueKind.Should().Be(JsonValueKind.String);
        firstCollection.GetProperty("success").GetBoolean().Should().BeFalse(
            "the first collection in sorted order (\"parks\") was the failed import.");
        firstCollection.GetProperty("target").ValueKind.Should().Be(JsonValueKind.String);
        firstCollection.GetProperty("featuresImported").ValueKind.Should().Be(JsonValueKind.Number);
        firstCollection.GetProperty("featuresSkipped").ValueKind.Should().Be(JsonValueKind.Number);
        firstCollection.GetProperty("pagesFetched").ValueKind.Should().Be(JsonValueKind.Number);
        firstCollection.GetProperty("truncated").GetBoolean().Should().BeFalse();
        firstCollection.GetProperty("filterScope").ValueKind.Should().Be(JsonValueKind.Object);
        firstCollection.GetProperty("mappingDiagnostics").ValueKind.Should().Be(JsonValueKind.Array);
    }

    private static OgcApiFeaturesMigrationEvidencePackInputs BuildInputs()
    {
        var source = new MigrationSourceIdentity
        {
            DisplayName = "OGC Sample Service",
            BaseUrl = "https://ogc.example.com/ogcapi",
            Product = "pygeoapi",
            Version = "0.16.0",
            ServiceType = "ogc-api-features"
        };

        var inventory = new MigrationSourceInventoryArtifact
        {
            SourceKind = "ogc-api-features",
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
                ResourceCount = 3
            },
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Source advertises OGC API Features Core conformance."
            },
            Resources =
            [
                CollectionResource("roads"),
                CollectionResource("rivers"),
                CollectionResource("parks")
            ],
            ExternalDependencies =
            [
                ConformanceDependency("http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core"),
                ConformanceDependency("http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson")
            ]
        };

        var roads = new OgcApiFeaturesMigrationEvidencePackCollectionInput
        {
            Result = new OgcApiFeaturesImportResult
            {
                Success = true,
                CollectionId = "roads",
                Target = "ogc.roads",
                FeaturesImported = 100,
                FeaturesSkipped = 1,
                PagesFetched = 2,
                Truncated = false,
                MappingDiagnostics =
                [
                    new OgcApiFeaturesSchemaMappingDiagnostic
                    {
                        PropertyName = "speed_limit",
                        SourceType = "integer",
                        TargetColumnType = "smallint",
                        Classification = OgcApiFeaturesSchemaMappingClassification.ManualReview,
                        Severity = "warning",
                        Reason = "narrowing conversion (integer → smallint)"
                    }
                ]
            }
        };

        var rivers = new OgcApiFeaturesMigrationEvidencePackCollectionInput
        {
            Result = new OgcApiFeaturesImportResult
            {
                Success = true,
                CollectionId = "rivers",
                Target = "ogc.rivers",
                FeaturesImported = 25,
                FeaturesSkipped = 1,
                PagesFetched = 3,
                Truncated = true,
                ScopeDriftDetected = true,
                ManualReviewReason =
                    "OGC API Features import scope changed since the previous run; manual reconciliation required.",
                MappingDiagnostics =
                [
                    new OgcApiFeaturesSchemaMappingDiagnostic
                    {
                        PropertyName = "tributary_of",
                        SourceType = "string",
                        TargetColumnType = null,
                        Classification = OgcApiFeaturesSchemaMappingClassification.Unsupported,
                        Severity = "error",
                        Reason = "no target column"
                    }
                ]
            },
            Filter = "name LIKE 'Sandy%'",
            Bbox = "-180,-90,180,90",
            Datetime = "2026-01-01T00:00:00Z/.."
        };

        var parks = new OgcApiFeaturesMigrationEvidencePackCollectionInput
        {
            Result = new OgcApiFeaturesImportResult
            {
                Success = false,
                CollectionId = "parks",
                Target = "ogc.parks",
                FeaturesImported = 0,
                FeaturesSkipped = 0,
                PagesFetched = 0,
                ErrorCode = OgcApiFeaturesImportErrorCodes.SourceUnreachable,
                ErrorMessage = "Items endpoint could not be reached."
            }
        };

        return new OgcApiFeaturesMigrationEvidencePackInputs
        {
            Inventory = inventory,
            CollectionResults = [roads, rivers, parks]
        };
    }

    private static MigrationInventoryResource CollectionResource(string id)
        => new()
        {
            Id = $"collection:{id}",
            ContainerId = "service:ogc-api-features",
            Kind = "ogc-api-features-collection",
            Name = id,
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Collection advertises GeoJSON items."
            }
        };

    private static MigrationExternalDependency ConformanceDependency(string conformanceClass)
        => new()
        {
            Id = $"conformance:{conformanceClass.GetHashCode():x}",
            ContainerId = "service:ogc-api-features",
            Kind = "ogc-api-features-conformance",
            Name = conformanceClass,
            DependencyType = "conformance",
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Conformance class is recognized by the OGC API Features migration path."
            }
        };
}
