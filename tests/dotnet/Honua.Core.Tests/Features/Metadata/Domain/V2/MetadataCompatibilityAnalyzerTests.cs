// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

[Protocol(Protocols.TestQuality)]
[Operation(Operations.Validation)]
public sealed class MetadataCompatibilityAnalyzerTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void Analyze_MatchingSourceAndTarget_ReturnsReadyMetadataOnlyReport()
    {
        var source = Snapshot(BuildGraph("dev", 41));
        var target = Snapshot(BuildGraph("staging", 7));
        var package = Package(Entry("res.parcels", MetadataSemanticArtifactKind.Resource));

        var report = MetadataCompatibilityAnalyzer.Analyze(
            package,
            source,
            target,
            Array.Empty<MetadataDataScriptEntry>(),
            GeneratedAt);

        report.Status.Should().Be(MetadataCompatibilityStatus.Ready);
        report.CanCreatePullRequest.Should().BeTrue();
        report.CanPromote.Should().BeTrue();
        report.Findings.Should().BeEmpty();
        report.RollbackReadiness.Classification.Should().Be(MetadataRollbackReadinessClassification.MetadataOnly);
    }

    [UnitTest]
    public void Analyze_MissingSchemaServicePublicationAndCrs_ReturnsBlockedFindings()
    {
        var source = Snapshot(BuildGraph("dev", 41, identifierRole: true, srid: 4326));
        var target = Snapshot(BuildGraph(
            "staging",
            7,
            includeField: false,
            srid: 3857,
            includeService: false,
            includePublication: false));
        var package = Package(
            Entry("res.parcels", MetadataSemanticArtifactKind.Resource),
            Entry("svc.features", MetadataSemanticArtifactKind.Service),
            Entry("pub.parcels", MetadataSemanticArtifactKind.Publication));

        var report = MetadataCompatibilityAnalyzer.Analyze(
            package,
            source,
            target,
            Array.Empty<MetadataDataScriptEntry>(),
            GeneratedAt);

        report.Status.Should().Be(MetadataCompatibilityStatus.Blocked);
        report.CanCreatePullRequest.Should().BeFalse();
        report.Findings.Should().Contain(finding => finding.Code == MetadataCompatibilityCode.FieldMissing);
        report.Findings.Should().Contain(finding => finding.Code == MetadataCompatibilityCode.IdentifierMissing);
        report.Findings.Should().Contain(finding => finding.Code == MetadataCompatibilityCode.SpatialCrsMismatch);
        report.Findings.Should().Contain(finding => finding.Code == MetadataCompatibilityCode.ServiceMissing);
        report.Findings.Should().Contain(finding => finding.Code == MetadataCompatibilityCode.PublicationMissing);
        report.Findings.Should().OnlyContain(finding =>
            finding.Expected.Value == null || !finding.Expected.Value.Contains("password", StringComparison.OrdinalIgnoreCase));
        report.RollbackReadiness.Classification.Should().Be(MetadataRollbackReadinessClassification.Manual);
    }

    [UnitTest]
    public void Analyze_ReversibleDataScriptCoversMissingField_ReturnsWarningAndScriptReversibleRollback()
    {
        var source = Snapshot(BuildGraph("dev", 41));
        var target = Snapshot(BuildGraph("staging", 7, includeField: false));
        var package = Package(Entry("res.parcels", MetadataSemanticArtifactKind.Resource));
        var scripts = new[]
        {
            MissingFieldScript(reversible: true, beforeFieldExists: false),
        };

        var report = MetadataCompatibilityAnalyzer.Analyze(
            package,
            source,
            target,
            scripts,
            GeneratedAt);

        report.Status.Should().Be(MetadataCompatibilityStatus.Warning);
        report.UncoveredErrorCount.Should().Be(0);
        report.CoveredErrorCount.Should().Be(1);
        report.CanCreatePullRequest.Should().BeTrue();
        report.Findings.Should().ContainSingle(finding =>
            finding.Code == MetadataCompatibilityCode.FieldMissing &&
            finding.CoverageState == MetadataCompatibilityCoverageState.CoveredByScript &&
            finding.CoveringScriptId == "script.add-apn");
        report.RollbackReadiness.Classification.Should().Be(MetadataRollbackReadinessClassification.ScriptReversible);
    }

    [UnitTest]
    public void Analyze_NonReversibleDataScriptCoversMissingField_ReturnsSnapshotRequiredRollback()
    {
        var source = Snapshot(BuildGraph("dev", 41));
        var target = Snapshot(BuildGraph("staging", 7, includeField: false));
        var package = Package(Entry("res.parcels", MetadataSemanticArtifactKind.Resource));

        var report = MetadataCompatibilityAnalyzer.Analyze(
            package,
            source,
            target,
            [MissingFieldScript(reversible: false, beforeFieldExists: false)],
            GeneratedAt);

        report.Status.Should().Be(MetadataCompatibilityStatus.Warning);
        report.RollbackReadiness.Classification.Should().Be(MetadataRollbackReadinessClassification.SnapshotRequired);
        report.RollbackReadiness.RequiresSnapshot.Should().BeTrue();
        report.RollbackReadiness.ScriptIds.Should().ContainSingle("script.add-apn");
    }

    [UnitTest]
    public void Analyze_ScriptBeforeContractMismatch_LeavesFindingBlocked()
    {
        var source = Snapshot(BuildGraph("dev", 41));
        var target = Snapshot(BuildGraph("staging", 7, includeField: false));
        var package = Package(Entry("res.parcels", MetadataSemanticArtifactKind.Resource));

        var report = MetadataCompatibilityAnalyzer.Analyze(
            package,
            source,
            target,
            [MissingFieldScript(reversible: true, beforeFieldExists: true)],
            GeneratedAt);

        report.Status.Should().Be(MetadataCompatibilityStatus.Blocked);
        report.Findings.Should().Contain(finding =>
            finding.Code == MetadataCompatibilityCode.FieldMissing &&
            finding.CoverageState == MetadataCompatibilityCoverageState.Uncovered);
        report.Findings.Should().Contain(finding =>
            finding.Code == MetadataCompatibilityCode.ScriptBeforeContractMismatch);
        report.RollbackReadiness.Classification.Should().Be(MetadataRollbackReadinessClassification.Manual);
    }

    [UnitTest]
    public void Analyze_ServiceRouteChange_ListsDependentsAndRequiresServiceRevisionRollback()
    {
        var source = Snapshot(BuildGraph("dev", 41, serviceRoute: "/shared/ogc/features"));
        var target = Snapshot(BuildGraph(
            "staging",
            7,
            serviceRoute: "/old/ogc/features",
            includeRelationshipDependent: true,
            includeProjectionProfile: true));
        var package = Package(
            Entry("res.parcels", MetadataSemanticArtifactKind.Resource, dependentIds: ["res.parcel-dashboard"]),
            Entry("svc.features", MetadataSemanticArtifactKind.Service));

        var report = MetadataCompatibilityAnalyzer.Analyze(
            package,
            source,
            target,
            Array.Empty<MetadataDataScriptEntry>(),
            GeneratedAt);

        report.Status.Should().Be(MetadataCompatibilityStatus.Warning);
        report.Findings.Should().ContainSingle(finding => finding.Code == MetadataCompatibilityCode.ServiceRouteChanged);
        report.AffectedDependents.Should().Contain(dependent =>
            dependent.SemanticId == "pub.parcels" &&
            dependent.RelationshipKind == "publication-by-resource");
        report.AffectedDependents.Should().Contain(dependent =>
            dependent.SemanticId == "res.parcel-dashboard" &&
            dependent.RelationshipKind == "release-package-dependent");
        report.AffectedDependents.Should().Contain(dependent =>
            dependent.SemanticId == "profile.ogc" &&
            dependent.RelationshipKind == "projection-required-semantics");
        report.RollbackReadiness.Classification.Should().Be(MetadataRollbackReadinessClassification.ServiceRevision);
    }

    [UnitTest]
    public void CreateUnavailableReport_ReturnsUnknownManualReport()
    {
        var report = MetadataCompatibilityAnalyzer.CreateUnavailableReport(
            null,
            "prod",
            GeneratedAt,
            "package.missing",
            "persisted release package",
            "not found",
            "The requested metadata release package was not found.",
            scriptCount: 0);

        report.Status.Should().Be(MetadataCompatibilityStatus.Unknown);
        report.CanCreatePullRequest.Should().BeFalse();
        report.Findings.Should().ContainSingle(finding =>
            finding.Code == MetadataCompatibilityCode.StateUnavailable &&
            finding.CoverageState == MetadataCompatibilityCoverageState.Unknown);
        report.RollbackReadiness.Classification.Should().Be(MetadataRollbackReadinessClassification.Manual);
    }

    private static MetadataReleasePackage Package(params MetadataReleaseEntry[] entries)
        => new()
        {
            PackageId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "pkg.parcels",
                Name = "promote-parcels",
                Title = "Promote parcels",
            },
            SourceEnvironment = "dev",
            SourceRevision = 41,
            SourceEtag = "etag-dev-41",
            TargetEnvironments = ["staging"],
            Entries = entries,
            CreatedBy = "tester",
            CreatedAt = GeneratedAt,
            UpdatedAt = GeneratedAt,
        };

    private static MetadataReleaseEntry Entry(
        string semanticId,
        MetadataSemanticArtifactKind kind,
        IReadOnlyList<string>? dependentIds = null)
        => new()
        {
            SemanticId = semanticId,
            ArtifactKind = kind,
            DesiredMetadataRevision = 41,
            DependentSemanticIds = dependentIds ?? Array.Empty<string>(),
        };

    private static MetadataDataScriptEntry MissingFieldScript(bool reversible, bool beforeFieldExists)
        => new()
        {
            ScriptId = "script.add-apn",
            Kind = "sql",
            Reversible = reversible,
            DeclaredOperations = ["add-field"],
            BeforeContract = new MetadataDataScriptContract
            {
                Resources =
                [
                    new MetadataScriptResourceContract
                    {
                        SemanticId = "res.parcels",
                        SemanticKind = MetadataSemanticArtifactKind.Resource,
                        Exists = true,
                        Fields =
                        [
                            new MetadataScriptFieldContract
                            {
                                SemanticId = "field.parcels.apn",
                                Name = "apn",
                                Exists = beforeFieldExists,
                                Type = "string",
                                Nullable = false,
                            },
                        ],
                    },
                ],
            },
            AfterContract = new MetadataDataScriptContract
            {
                Resources =
                [
                    new MetadataScriptResourceContract
                    {
                        SemanticId = "res.parcels",
                        SemanticKind = MetadataSemanticArtifactKind.Resource,
                        Exists = true,
                        Fields =
                        [
                            new MetadataScriptFieldContract
                            {
                                SemanticId = "field.parcels.apn",
                                Name = "apn",
                                Exists = true,
                                Type = "string",
                                Nullable = false,
                            },
                        ],
                    },
                ],
            },
        };

    private static MetadataV2GraphSnapshot Snapshot(MetadataV2Graph graph)
        => new(graph, $"etag-{graph.Environment}-{graph.Revision}", GeneratedAt);

    private static MetadataV2Graph BuildGraph(
        string environment,
        long revision,
        bool includeField = true,
        bool identifierRole = false,
        int srid = 4326,
        bool includeService = true,
        bool includePublication = true,
        string serviceRoute = "/shared/ogc/features",
        bool includeRelationshipDependent = false,
        bool includeProjectionProfile = false)
    {
        var spatial = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"srid":{{srid}},"geometryType":"Point"}""");

        var resources = new List<MetadataV2Resource>
        {
            new()
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = "res.parcels",
                    Name = "parcels",
                    Title = "Parcels",
                },
                Type = MetadataV2ResourceType.FeatureDataset,
                StorageBindingIds = ["storage.parcels"],
                PrimaryStorageBindingId = "storage.parcels",
                SchemaFields = includeField ? [BuildField(identifierRole)] : Array.Empty<MetadataV2Field>(),
                Spatial = spatial,
            },
        };

        if (includeRelationshipDependent)
        {
            resources.Add(new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = "res.parcel-dashboard",
                    Name = "parcel-dashboard",
                    Title = "Parcel Dashboard",
                },
                Type = MetadataV2ResourceType.Dashboard,
                Relationships =
                [
                    new MetadataV2Relationship
                    {
                        Id = "rel.dashboard.parcels",
                        Name = "Dashboard parcels",
                        RelatedResourceId = "res.parcels",
                    },
                ],
            });
        }

        return new MetadataV2Graph
        {
            Environment = environment,
            Revision = revision,
            GeneratedAt = GeneratedAt,
            Resources = resources,
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage.parcels", Name = "storage.parcels" },
                    ResourceId = "res.parcels",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "shared.parcels",
                    Capabilities = [MetadataV2StorageBindingCapability.Query],
                },
            ],
            Services = includeService
                ?
                [
                    new MetadataV2Service
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "svc.features", Name = "features" },
                        ServiceType = MetadataV2ServiceType.OgcApiFeatures,
                        Route = serviceRoute,
                        PublicationIds = includePublication ? ["pub.parcels"] : Array.Empty<string>(),
                    },
                ]
                : Array.Empty<MetadataV2Service>(),
            Publications = includePublication
                ?
                [
                    new MetadataV2Publication
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "pub.parcels", Name = "parcels" },
                        ResourceId = "res.parcels",
                        ServiceId = "svc.features",
                        StorageBindingId = "storage.parcels",
                        PublicationType = MetadataV2PublicationType.OgcCollection,
                        Path = "parcels",
                        ServiceLocalId = "parcels",
                    },
                ]
                : Array.Empty<MetadataV2Publication>(),
            ProjectionProfiles = includeProjectionProfile
                ?
                [
                    new MetadataV2ProjectionProfile
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "profile.ogc", Name = "ogc" },
                        Target = "ogc-api-features",
                        RequiredSemantics = ["res.parcels"],
                    },
                ]
                : Array.Empty<MetadataV2ProjectionProfile>(),
        };
    }

    private static MetadataV2Field BuildField(bool identifierRole)
        => new()
        {
            SemanticId = "field.parcels.apn",
            Name = "apn",
            Type = "string",
            Nullable = false,
            SemanticRoles = identifierRole ? ["identifier.primary"] : Array.Empty<string>(),
        };
}
