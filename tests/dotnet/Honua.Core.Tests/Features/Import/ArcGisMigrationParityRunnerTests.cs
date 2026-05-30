// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class ArcGisMigrationParityRunnerTests
{
    private const string ArcGisSourceKind = "arcgis-geoservices-rest";
    private const string SourceResourceId = "resource:Inspections:layer:0";
    private const string TargetResourceId = "target:resource:inspections:inspection-points";

    [Fact]
    public async Task RunAsync_AllProbesWithinPassBands_ClassifiesPass()
    {
        var manifest = BuildManifest(
            sourceFeatureCount: 100,
            fields: ["NAME", "CATEGORY"],
            attachments:
            [
                AutomatedAttachment("att-1"),
                AutomatedAttachment("att-2")
            ],
            relationships:
            [
                AssistedRelationship("rel-1")
            ]);
        var inventory = BuildInventory(sourceFeatureCount: 100);
        var reader = new StubParityReader
        {
            FeatureCount = 100,
            FieldNames = ["NAME", "CATEGORY", "OBJECTID"]
        };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        artifact.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        artifact.ArtifactKind.Should().Be("honua.migration.arcgis-parity");
        artifact.ResourceProbes.Should().ContainSingle();

        var probe = artifact.ResourceProbes[0];
        probe.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        probe.FeatureCount.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        probe.FeatureCount.Delta.Should().Be(0);
        probe.Schema.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        probe.Schema.MissingFieldNames.Should().BeEmpty();
        probe.IdentityCoverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        probe.AttachmentCoverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        probe.AttachmentCoverage.Coverage.Should().Be(1.0);
        probe.RelationshipCoverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
    }

    [Theory]
    [InlineData(100, 103, ArcGisMigrationParityClassifications.Pass)] // 3% delta within 5%
    [InlineData(100, 105, ArcGisMigrationParityClassifications.Pass)] // exactly 5% delta -> pass
    [InlineData(100, 110, ArcGisMigrationParityClassifications.Warn)] // 10% delta within 20%
    [InlineData(100, 120, ArcGisMigrationParityClassifications.Warn)] // exactly 20% delta -> warn
    [InlineData(100, 130, ArcGisMigrationParityClassifications.Fail)] // 30% delta -> fail
    [InlineData(100, 50, ArcGisMigrationParityClassifications.Fail)]  // 50% drop -> fail
    public async Task RunAsync_FeatureCountProbe_BandsMatchClassification(
        int sourceCount,
        long targetCount,
        string expectedClassification)
    {
        var manifest = BuildManifest(sourceFeatureCount: sourceCount, fields: ["NAME"]);
        var inventory = BuildInventory(sourceFeatureCount: sourceCount);
        var reader = new StubParityReader
        {
            FeatureCount = targetCount,
            FieldNames = ["NAME"]
        };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        artifact.ResourceProbes[0].FeatureCount.Classification.Should().Be(expectedClassification);
        artifact.ResourceProbes[0].FeatureCount.SourceCount.Should().Be(sourceCount);
        artifact.ResourceProbes[0].FeatureCount.TargetCount.Should().Be(targetCount);
        artifact.ResourceProbes[0].FeatureCount.Delta.Should().Be(targetCount - sourceCount);
    }

    [Fact]
    public async Task RunAsync_FeatureCountProbe_MissingSourceCount_RecordsWarn()
    {
        var manifest = BuildManifest(sourceFeatureCount: null, fields: ["NAME"]);
        var inventory = BuildInventory(sourceFeatureCount: null);
        var reader = new StubParityReader
        {
            FeatureCount = 100,
            FieldNames = ["NAME"]
        };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        artifact.ResourceProbes[0].FeatureCount.Classification.Should().Be(ArcGisMigrationParityClassifications.Warn);
        artifact.ResourceProbes[0].FeatureCount.SourceCount.Should().BeNull();
        artifact.ResourceProbes[0].FeatureCount.Reason.Should().Contain("did not advertise");
    }

    [Fact]
    public async Task RunAsync_FeatureCountProbe_TargetNotFound_RecordsFail()
    {
        var manifest = BuildManifest(sourceFeatureCount: 50, fields: ["NAME"]);
        var inventory = BuildInventory(sourceFeatureCount: 50);
        var reader = new StubParityReader
        {
            FeatureCount = null,
            FieldNames = null
        };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var probe = artifact.ResourceProbes[0];
        probe.FeatureCount.Classification.Should().Be(ArcGisMigrationParityClassifications.Fail);
        probe.FeatureCount.Reason.Should().Contain("not found");
        artifact.Classification.Should().Be(ArcGisMigrationParityClassifications.Fail);
    }

    [Fact]
    public async Task RunAsync_SchemaProbe_AllExpectedFieldsPresent_ClassifiesPass()
    {
        var manifest = BuildManifest(sourceFeatureCount: 10, fields: ["NAME", "CATEGORY"]);
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader
        {
            FeatureCount = 10,
            FieldNames = ["NAME", "CATEGORY", "OBJECTID"]
        };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var schema = artifact.ResourceProbes[0].Schema;
        schema.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        schema.ExpectedFieldNames.Should().BeEquivalentTo(["CATEGORY", "NAME"]);
        schema.MissingFieldNames.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SchemaProbe_MissingField_ClassifiesFail()
    {
        var manifest = BuildManifest(sourceFeatureCount: 10, fields: ["NAME", "CATEGORY"]);
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader
        {
            FeatureCount = 10,
            FieldNames = ["NAME"] // CATEGORY missing
        };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var schema = artifact.ResourceProbes[0].Schema;
        schema.Classification.Should().Be(ArcGisMigrationParityClassifications.Fail);
        schema.MissingFieldNames.Should().Equal(["CATEGORY"]);
        schema.Reason.Should().Contain("CATEGORY");
        artifact.Classification.Should().Be(ArcGisMigrationParityClassifications.Fail);
    }

    [Fact]
    public async Task RunAsync_AttachmentCoverageProbe_PartialCoverage_ClassifiesWarn()
    {
        // 5 automated attachments, 4 with target refs -> 80% coverage -> warn (>= 0.80 and < 0.99)
        var manifest = BuildManifest(
            sourceFeatureCount: 10,
            fields: ["NAME"],
            attachments:
            [
                AutomatedAttachment("att-1"),
                AutomatedAttachment("att-2"),
                AutomatedAttachment("att-3"),
                AutomatedAttachment("att-4"),
                AutomatedAttachment("att-5", targetRef: null) // missing target ref
            ]);
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader { FeatureCount = 10, FieldNames = ["NAME"] };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var coverage = artifact.ResourceProbes[0].AttachmentCoverage;
        coverage.Expected.Should().Be(5);
        coverage.Observed.Should().Be(4);
        coverage.Coverage.Should().BeApproximately(0.8, 1e-9);
        coverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Warn);
        coverage.MissingIds.Should().Equal(["att-5"]);
        artifact.Classification.Should().Be(ArcGisMigrationParityClassifications.Warn);
    }

    [Fact]
    public async Task RunAsync_AttachmentCoverageProbe_BelowWarnBand_ClassifiesFail()
    {
        // 4 automated attachments, 1 with target ref -> 25% coverage -> fail (< 0.80)
        var manifest = BuildManifest(
            sourceFeatureCount: 10,
            fields: ["NAME"],
            attachments:
            [
                AutomatedAttachment("att-1"),
                AutomatedAttachment("att-2", targetRef: null),
                AutomatedAttachment("att-3", targetRef: null),
                AutomatedAttachment("att-4", targetRef: null)
            ]);
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader { FeatureCount = 10, FieldNames = ["NAME"] };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var coverage = artifact.ResourceProbes[0].AttachmentCoverage;
        coverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Fail);
        coverage.Coverage.Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public async Task RunAsync_AttachmentCoverageProbe_OnlyConsidersAutomatedClassification()
    {
        // 1 automated (covered) + 2 manual-review (ignored) + 1 unsupported (ignored) -> 100% coverage
        var manifest = BuildManifest(
            sourceFeatureCount: 10,
            fields: ["NAME"],
            attachments:
            [
                AutomatedAttachment("att-1"),
                Attachment("att-2", MigrationManifestAttachmentClassifications.ManualReview, targetRef: null),
                Attachment("att-3", MigrationManifestAttachmentClassifications.ManualReview, targetRef: null),
                Attachment("att-4", MigrationManifestAttachmentClassifications.Unsupported, targetRef: null)
            ]);
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader { FeatureCount = 10, FieldNames = ["NAME"] };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var coverage = artifact.ResourceProbes[0].AttachmentCoverage;
        coverage.Expected.Should().Be(1);
        coverage.Observed.Should().Be(1);
        coverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
    }

    [Fact]
    public async Task RunAsync_RelationshipCoverageProbe_ConsidersAutomatedAndAssistedOnly()
    {
        // 2 automated + 2 assisted + 1 manual-review (ignored)
        // automated/assisted: 4 expected, 3 covered (one missing target ref) -> 75% -> fail
        var manifest = BuildManifest(
            sourceFeatureCount: 10,
            fields: ["NAME"],
            relationships:
            [
                Relationship("rel-1", MigrationManifestRelationshipClassifications.Automated),
                Relationship("rel-2", MigrationManifestRelationshipClassifications.Automated, targetRef: null),
                Relationship("rel-3", MigrationManifestRelationshipClassifications.Assisted),
                Relationship("rel-4", MigrationManifestRelationshipClassifications.Assisted),
                Relationship("rel-5", MigrationManifestRelationshipClassifications.ManualReview, targetRef: null)
            ]);
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader { FeatureCount = 10, FieldNames = ["NAME"] };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var coverage = artifact.ResourceProbes[0].RelationshipCoverage;
        coverage.Expected.Should().Be(4);
        coverage.Observed.Should().Be(3);
        coverage.Coverage.Should().BeApproximately(0.75, 1e-9);
        coverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Fail);
        coverage.MissingIds.Should().Equal(["rel-2"]);
    }

    [Fact]
    public async Task RunAsync_IdentityCoverageProbe_IncludesAttachmentAndRelationshipBindings()
    {
        var manifest = BuildManifest(
            sourceFeatureCount: 10,
            fields: ["NAME"],
            attachments:
            [
                AutomatedAttachment("att-1"),
                AutomatedAttachment("att-2")
            ],
            relationships:
            [
                AssistedRelationship("rel-1")
            ]);
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader { FeatureCount = 10, FieldNames = ["NAME"] };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var coverage = artifact.ResourceProbes[0].IdentityCoverage;
        // expected ids = resource (1) + attachments (2) + relationships (1) = 4
        coverage.Expected.Should().Be(4);
        coverage.Observed.Should().Be(4);
        coverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
    }

    [Fact]
    public async Task RunAsync_CoverageProbe_NoExpectedItems_ClassifiesPass()
    {
        var manifest = BuildManifest(sourceFeatureCount: 10, fields: ["NAME"]);
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader { FeatureCount = 10, FieldNames = ["NAME"] };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var probe = artifact.ResourceProbes[0];
        probe.AttachmentCoverage.Expected.Should().Be(0);
        probe.AttachmentCoverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        probe.RelationshipCoverage.Expected.Should().Be(0);
        probe.RelationshipCoverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
    }

    [Fact]
    public async Task RunAsync_AggregateClassification_FailDominatesWarnAndPass()
    {
        var manifest = BuildManifest(
            sourceFeatureCount: 100,
            fields: ["NAME", "CATEGORY"],
            attachments: [AutomatedAttachment("att-1", targetRef: null)]); // 0% coverage -> fail
        var inventory = BuildInventory(sourceFeatureCount: 100);
        var reader = new StubParityReader { FeatureCount = 110, FieldNames = ["NAME", "CATEGORY"] }; // 10% -> warn

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        artifact.Classification.Should().Be(ArcGisMigrationParityClassifications.Fail);
        artifact.Reasons.Should().NotBeEmpty();
        artifact.Reasons.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsNonArcGisManifest()
    {
        var manifest = BuildManifest(sourceFeatureCount: 10, fields: ["NAME"]) with { SourceKind = "geoserver-rest" };
        var inventory = BuildInventory(sourceFeatureCount: 10);
        var reader = new StubParityReader { FeatureCount = 10, FieldNames = ["NAME"] };

        var act = async () => await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*arcgis-geoservices-rest*");
    }

    [Fact]
    public async Task RunAsync_Artifact_RoundTripsThroughSystemTextJson()
    {
        var manifest = BuildManifest(
            sourceFeatureCount: 100,
            fields: ["NAME"],
            attachments: [AutomatedAttachment("att-1")],
            relationships: [AssistedRelationship("rel-1")]);
        var inventory = BuildInventory(sourceFeatureCount: 100);
        var reader = new StubParityReader { FeatureCount = 110, FieldNames = ["NAME"] };

        var artifact = await ArcGisMigrationParityRunner.RunAsync(manifest, inventory, reader);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(artifact, options);
        json.Should().Contain("\"artifactKind\":\"honua.migration.arcgis-parity\"");
        json.Should().Contain("\"classification\":\"warn\"");
        json.Should().Contain("\"resourceProbes\":");
        json.Should().Contain("\"featureCount\":");
        json.Should().Contain("\"schema\":");
        json.Should().Contain("\"identityCoverage\":");
        json.Should().Contain("\"attachmentCoverage\":");
        json.Should().Contain("\"relationshipCoverage\":");

        var round = JsonSerializer.Deserialize<ArcGisMigrationParityArtifact>(json, options);
        round.Should().NotBeNull();
        round!.Classification.Should().Be(ArcGisMigrationParityClassifications.Warn);
        round.ResourceProbes.Should().HaveCount(1);
        round.ResourceProbes[0].FeatureCount.Delta.Should().Be(10);
        round.ResourceProbes[0].FeatureCount.Classification.Should().Be(ArcGisMigrationParityClassifications.Warn);
        round.ResourceProbes[0].AttachmentCoverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
        round.ResourceProbes[0].RelationshipCoverage.Classification.Should().Be(ArcGisMigrationParityClassifications.Pass);
    }

    // ----- Fixtures -----

    private sealed class StubParityReader : IArcGisParityFeatureReader
    {
        public long? FeatureCount { get; init; }
        public IReadOnlyList<string>? FieldNames { get; init; }

        public Task<long?> GetFeatureCountAsync(string targetResourceId, CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureCount);

        public Task<IReadOnlyList<string>?> GetFieldNamesAsync(string targetResourceId, CancellationToken cancellationToken = default)
            => Task.FromResult(FieldNames);
    }

    private static MigrationManifestAttachmentRecord AutomatedAttachment(string id, string? targetRef = "default")
        => Attachment(id, MigrationManifestAttachmentClassifications.Automated, targetRef);

    private static MigrationManifestAttachmentRecord Attachment(string id, string classification, string? targetRef)
        => new()
        {
            SourceAttachmentId = id,
            SourceResourceId = SourceResourceId,
            Classification = classification,
            TargetAttachmentRef = targetRef == "default"
                ? $"target:attachment:inspections:inspection-points:{id}"
                : targetRef
        };

    private static MigrationManifestRelationshipRecord AssistedRelationship(string id)
        => Relationship(id, MigrationManifestRelationshipClassifications.Assisted);

    private static MigrationManifestRelationshipRecord Relationship(string id, string classification, string? targetRef = "default")
        => new()
        {
            SourceRelationshipId = id,
            SourceResourceId = SourceResourceId,
            Classification = classification,
            TargetRelationshipRef = targetRef == "default"
                ? $"target:relationship:inspections:inspection-points:{id}"
                : targetRef
        };

    private static MigrationManifestArtifact BuildManifest(
        int? sourceFeatureCount,
        string[] fields,
        MigrationManifestAttachmentRecord[]? attachments = null,
        MigrationManifestRelationshipRecord[]? relationships = null)
    {
        var manifestFields = fields
            .Select(name => new MigrationInventoryField
            {
                Name = name,
                FieldType = "esriFieldTypeString",
                Nullable = true
            })
            .ToArray();

        var compatibility = new MigrationCompatibilityAssessment
        {
            Level = "compatible",
            Reason = "Layer can be represented.",
            ManualSteps = []
        };

        var targetResource = new MigrationManifestTargetResource
        {
            SourceResourceId = SourceResourceId,
            SourceKind = "layer",
            Action = "publish",
            TargetResourceId = TargetResourceId,
            TargetServiceName = "inspections",
            TargetResourceName = "inspection-points",
            GeometryType = "Point",
            Fields = manifestFields,
            Capabilities = ["Query"],
            Attachments = attachments ?? [],
            Relationships = relationships ?? [],
            Identity = new MigrationManifestResourceIdentity
            {
                SourceServiceId = "Inspections",
                SourceLayerId = "0",
                TargetServiceId = "inspections",
                TargetLayerId = "0",
                IdentityStability = MigrationManifestIdentityStabilities.Preserved
            },
            Compatibility = compatibility
        };

        var remaps = new List<MigrationManifestIdentityRemap>
        {
            new()
            {
                SourceId = SourceResourceId,
                SourceKind = "layer",
                TargetId = TargetResourceId,
                TargetKind = "resource",
                TargetName = "inspection-points",
                Action = "publish",
                IdentityStability = MigrationManifestIdentityStabilities.Preserved
            }
        };

        foreach (var attachment in targetResource.Attachments)
        {
            if (!string.IsNullOrWhiteSpace(attachment.TargetAttachmentRef))
            {
                remaps.Add(new MigrationManifestIdentityRemap
                {
                    SourceId = attachment.SourceAttachmentId,
                    SourceKind = "attachment",
                    TargetId = attachment.TargetAttachmentRef!,
                    TargetKind = "attachment",
                    TargetName = attachment.SourceAttachmentId,
                    Action = "publish",
                    IdentityStability = MigrationManifestIdentityStabilities.Preserved
                });
            }
        }

        foreach (var relationship in targetResource.Relationships)
        {
            if (!string.IsNullOrWhiteSpace(relationship.TargetRelationshipRef))
            {
                remaps.Add(new MigrationManifestIdentityRemap
                {
                    SourceId = relationship.SourceRelationshipId,
                    SourceKind = "relationship",
                    TargetId = relationship.TargetRelationshipRef!,
                    TargetKind = "relationship",
                    TargetName = relationship.SourceRelationshipId,
                    Action = "publish",
                    IdentityStability = MigrationManifestIdentityStabilities.Preserved
                });
            }
        }

        return new MigrationManifestArtifact
        {
            SourceKind = ArcGisSourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = "ArcGIS Source",
                BaseUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
                Product = "ArcGIS",
                Version = "11.2",
                ServiceType = "FeatureServer"
            },
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = 1,
                TargetResourceCount = 1
            },
            TargetResources = [targetResource],
            IdentityRemaps = remaps.ToArray()
        };
    }

    private static MigrationSourceInventoryArtifact BuildInventory(int? sourceFeatureCount)
    {
        var compatibility = new MigrationCompatibilityAssessment
        {
            Level = "compatible",
            Reason = "Layer can be represented.",
            ManualSteps = []
        };

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = ArcGisSourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = "ArcGIS Source",
                BaseUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
                Product = "ArcGIS",
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
                ResourceCount = 1
            },
            OverallCompatibility = compatibility,
            Containers =
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Inspections",
                    Kind = "feature-service",
                    Name = "Inspections",
                    Compatibility = compatibility
                }
            ],
            Resources =
            [
                new MigrationInventoryResource
                {
                    Id = SourceResourceId,
                    ContainerId = "service:Inspections",
                    Kind = "layer",
                    Name = "Inspection Points",
                    GeometryType = "Point",
                    FeatureCount = sourceFeatureCount,
                    Capabilities = ["Query"],
                    Fields = [],
                    Compatibility = compatibility
                }
            ]
        };
    }
}
