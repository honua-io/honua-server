// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Composite migration writes keep ownership on the canonical graph or stay deferred.
/// </summary>
public sealed class QueryOnlyCompositeRelationshipApplyTests
{
    [UnitTest]
    public void PlanCompositeRelationships_ReciprocalQueryOnlyPair_RetainsCompositeOwnership()
    {
        var plan = PostgresMigrationCatalogWriter.PlanCompositeRelationships(
            Graph(Resource(10), Resource(11)),
            [Request("origin", 10, 11, "esriRelRoleOrigin"), Request("destination", 11, 10, "esriRelRoleDestination")]);

        plan.Changed.Should().BeTrue();
        plan.Rejected.Should().BeEmpty();
        MetadataV2GraphValidator.Validate(plan.Candidate).IsValid.Should().BeTrue();
        plan.Candidate.Resources.Should().OnlyContain(resource =>
            resource.Relationships.Count == 1 && resource.Relationships[0].Composite);
        plan.Candidate.Resources.Select(resource => resource.Relationships[0].Role)
            .Should().BeEquivalentTo("esriRelRoleOrigin", "esriRelRoleDestination");
        plan.Candidate.Resources.Should().OnlyContain(resource =>
            resource.Relationships[0].OriginField == "Join_ID"
            && resource.Relationships[0].DestinationField == "Join_ID"
            && resource.Relationships[0].Cardinality == "one-to-many");

        var replay = PostgresMigrationCatalogWriter.PlanCompositeRelationships(
            plan.Candidate,
            [Request("origin", 10, 11, "esriRelRoleOrigin"), Request("destination", 11, 10, "esriRelRoleDestination")]);
        replay.Changed.Should().BeFalse();
        replay.Rejected.Should().BeEmpty();
        replay.Accepted.Should().OnlyContain(decision =>
            decision.Outcome == MigrationCatalogWriteOutcome.AlreadyExists && !decision.Deferred);
        replay.Candidate.Resources.Should().OnlyContain(resource => resource.Relationships[0].Composite);
    }

    [UnitTest]
    public void PlanCompositeRelationships_OneSidedEditableOrSimpleExisting_DoesNotWrite()
    {
        var oneSided = PostgresMigrationCatalogWriter.PlanCompositeRelationships(
            Graph(Resource(10), Resource(11)),
            [Request("origin", 10, 11, "esriRelRoleOrigin")]);
        oneSided.Changed.Should().BeFalse();
        oneSided.Accepted.Should().BeEmpty();
        oneSided.Rejected.Should().ContainSingle().Which.Message.Should().Contain("not rewritten as a simple relationship");
        oneSided.Candidate.Resources.Should().OnlyContain(resource => resource.Relationships.Count == 0);

        var editable = PostgresMigrationCatalogWriter.PlanCompositeRelationships(
            Graph(Resource(10, canModify: true), Resource(11)),
            [Request("origin", 10, 11, "esriRelRoleOrigin"), Request("destination", 11, 10, "esriRelRoleDestination")]);
        editable.Changed.Should().BeFalse();
        editable.Rejected.Should().HaveCount(2);
        editable.Rejected.Should().OnlyContain(decision => decision.Deferred && decision.Message.Contains("cannot enable editing", StringComparison.Ordinal));
        editable.Candidate.Resources.Should().OnlyContain(resource => resource.Relationships.Count == 0);

        var existingSimple = Resource(10) with
        {
            Relationships =
            [
                new MetadataV2Relationship
                {
                    Id = "rel-10-0",
                    Name = "Summary",
                    RelatedResourceId = "res-layer-11",
                    Role = "esriRelRoleOrigin",
                    Cardinality = "one-to-many",
                    OriginField = "Join_ID",
                    DestinationField = "Join_ID",
                    EsriRelationshipId = 0,
                    Composite = false
                }
            ]
        };
        var downgraded = PostgresMigrationCatalogWriter.PlanCompositeRelationships(
            Graph(existingSimple, Resource(11)),
            [Request("origin", 10, 11, "esriRelRoleOrigin")]);
        downgraded.Changed.Should().BeFalse();
        downgraded.Rejected.Should().ContainSingle().Which.Message.Should().Contain("not rewritten as a simple relationship");
        downgraded.Candidate.Resources.Single(resource => resource.Metadata.Id == "res-layer-10")
            .Relationships.Single().Composite.Should().BeFalse();
    }

    [UnitTest]
    public async Task EnsureRelationshipsAsync_WithoutGraphStore_DefersCompositeWithoutOpeningSql()
    {
        var writer = new PostgresMigrationCatalogWriter(NullLogger<PostgresMigrationCatalogWriter>.Instance);
        var outcomes = await writer.EnsureRelationshipsAsync(
            "Host=invalid;Database=unused",
            graphStore: null,
            [Request("origin", 10, 11, "esriRelRoleOrigin")],
            CancellationToken.None);

        var outcome = outcomes.Should().ContainSingle().Subject;
        outcome.Deferred.Should().BeTrue();
        outcome.Outcome.Should().Be(MigrationCatalogWriteOutcome.AlreadyExists);
        outcome.Message.Should().Contain("not rewritten as a simple relationship");
        outcome.TargetRelationshipRef.Should().Be("rel-10-0");
    }

    [UnitTest]
    public async Task ApplyPlannedCompositeRelationshipsAsync_RetriesStaleEtagAgainstFreshSnapshot()
    {
        var store = new ScriptedGraphStore(
            Snapshot("etag-1", Graph(Resource(10), Resource(11))),
            Snapshot("etag-2", Graph(Resource(10), Resource(11), Resource(99, name: "unrelated"))))
        {
            FailuresRemaining = 1
        };

        var plan = await PostgresMigrationCatalogWriter.ApplyPlannedCompositeRelationshipsAsync(
            store,
            [Request("origin", 10, 11, "esriRelRoleOrigin"), Request("destination", 11, 10, "esriRelRoleDestination")],
            CancellationToken.None);

        plan.Changed.Should().BeTrue();
        store.Saves.Should().ContainSingle();
        store.Saves[0].Etag.Should().Be("etag-2");
        store.Saves[0].Graph.Resources.Should().Contain(resource => resource.Metadata.Id == "res-layer-99" && resource.Metadata.Name == "unrelated");
        store.Saves[0].Graph.Resources.Where(resource => resource.Metadata.Id is "res-layer-10" or "res-layer-11")
            .Should().OnlyContain(resource => resource.Relationships.Single().Composite);
        store.AttemptedEtags.Should().Equal("etag-1", "etag-2");
    }

    private static MetadataV2GraphSnapshot Snapshot(string etag, MetadataV2Graph graph)
        => new(graph, etag, DateTimeOffset.UnixEpoch);

    private static MetadataV2Graph Graph(params MetadataV2Resource[] resources)
        => new() { Revision = 4, Resources = resources };

    private static MetadataV2Resource Resource(int layerId, bool canModify = false, string? name = null)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"res-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                Name = name ?? $"layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            },
            SchemaFields = [new MetadataV2Field { Name = "Join_ID", Type = MetadataV2FieldType.Integer }],
            Editing = new MetadataV2ResourceEditing { CanModify = canModify }
        };

    private static MigrationRelationshipApplyRequest Request(string id, int origin, int related, string role)
        => new()
        {
            SourceRelationshipId = id,
            EsriRelationshipId = 0,
            Name = "Summary",
            OriginLayerId = origin,
            RelatedLayerId = related,
            OriginKeyField = "Join_ID",
            DestinationKeyField = "Join_ID",
            Role = role,
            Cardinality = "1:N",
            Composite = true
        };

    private sealed class ScriptedGraphStore : IMetadataV2GraphStore
    {
        private readonly Queue<MetadataV2GraphSnapshot> _reads;

        public ScriptedGraphStore(params MetadataV2GraphSnapshot[] snapshots)
        {
            _reads = new Queue<MetadataV2GraphSnapshot>(snapshots);
        }

        public int FailuresRemaining { get; set; }

        public List<(MetadataV2Graph Graph, string? Etag)> Saves { get; } = [];

        public List<string?> AttemptedEtags { get; } = [];

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_reads.Dequeue());

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MetadataV2GraphSnapshot> SaveAsync(
            MetadataV2Graph graph,
            string? expectedEtag,
            CancellationToken cancellationToken = default)
        {
            AttemptedEtags.Add(expectedEtag);
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Metadata v2 etag mismatch.");
            }

            Saves.Add((graph, expectedEtag));
            return Task.FromResult(new MetadataV2GraphSnapshot(graph, "saved", DateTimeOffset.UnixEpoch));
        }

        public Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(
            long revision,
            string? expectedCurrentEtag,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
