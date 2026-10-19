// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Edit;

/// <summary>
/// The owner-policy read must see a masked owner field and must not publish it (honua-server#5426).
/// </summary>
public sealed class OwnerEditPolicyFieldAccessTests
{
    private const string Secret = "do-not-leak";

    private static readonly MetadataV2OwnerEditPolicy Policy = new()
    {
        Enabled = true,
        OwnerField = "owner"
    };

    [UnitTest]
    public async Task AuthorizeAsync_MaskedOwner_AllowsTheOwnerAndRefusesAnother()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);

        var owner = await OwnerEditPolicyFieldAccess.AuthorizeAsync(
            reader,
            layerId: 7,
            storageFeatureId: 42,
            Policy,
            AttributeRuleEditEvent.Update,
            new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false));

        var other = await OwnerEditPolicyFieldAccess.AuthorizeAsync(
            reader,
            layerId: 7,
            storageFeatureId: 42,
            Policy,
            AttributeRuleEditEvent.Delete,
            new EditPrincipal("bob", IsAuthenticated: true, IsAdmin: false));

        owner.IsAllowed.Should().BeTrue();
        owner.Reason.Should().BeNull();
        other.IsAllowed.Should().BeFalse();
        other.Reason.Should().NotContain(Secret);
        other.Reason.Should().NotContain("alice");
        reader.QueryCount.Should().Be(2);
        AssertPolicyQuery(reader.LastQuery);
    }

    [UnitTest]
    public async Task AuthorizeAsync_RowHiddenFromReader_IsRefused()
    {
        var reader = new RecordingReader(RecordingReader.Mode.Hidden);

        var decision = await OwnerEditPolicyFieldAccess.AuthorizeAsync(
            reader,
            layerId: 7,
            storageFeatureId: 42,
            Policy,
            AttributeRuleEditEvent.Update,
            new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false));

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().NotContain(Secret);
        AssertPolicyQuery(reader.LastQuery);
    }

    [UnitTest]
    public async Task AuthorizeAsync_InsertAdminAndAnonymous_DoNotReadTheOwner()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var alice = new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false);

        var insert = await OwnerEditPolicyFieldAccess.AuthorizeAsync(
            reader, 7, storageFeatureId: null, Policy, AttributeRuleEditEvent.Insert, alice);
        var admin = await OwnerEditPolicyFieldAccess.AuthorizeAsync(
            reader,
            7,
            42,
            Policy,
            AttributeRuleEditEvent.Update,
            new EditPrincipal("root", IsAuthenticated: true, IsAdmin: true));
        var anonymous = await OwnerEditPolicyFieldAccess.AuthorizeAsync(
            reader, 7, 42, Policy, AttributeRuleEditEvent.Delete, EditPrincipal.Anonymous);
        var disabled = await OwnerEditPolicyFieldAccess.AuthorizeAsync(
            reader,
            7,
            42,
            Policy with { Enabled = false },
            AttributeRuleEditEvent.Update,
            alice);

        insert.IsAllowed.Should().BeTrue();
        admin.IsAllowed.Should().BeTrue();
        anonymous.IsAllowed.Should().BeFalse();
        disabled.IsAllowed.Should().BeTrue();
        reader.QueryCount.Should().Be(0);
    }

    private static void AssertPolicyQuery(FeatureQuery query)
    {
        query.EnforcedSqlFilter.Should().BeNull();
        var maskedFields = query.EnforcedMaskedFields
            ?? throw new InvalidOperationException("policy query omitted the field mask");
        maskedFields.IsDefault.Should().BeFalse();
        maskedFields.IsEmpty.Should().BeTrue();
        var objectIds = query.ObjectIds
            ?? throw new InvalidOperationException("policy query omitted object ids");
        objectIds.Should().Equal(42L);
        var outFields = query.OutFields
            ?? throw new InvalidOperationException("policy query omitted out fields");
        outFields.Should().Equal("owner");
        query.Limit.Should().Be(1);
    }

    [UnitTest]
    public async Task ReadOwnersAsync_TwoRows_UsesOneUnmaskedQuery()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);

        var owners = await OwnerEditPolicyFieldAccess.ReadOwnersAsync(
            reader,
            layerId: 7,
            storageFeatureIds: [42L, 43L],
            ownerField: "owner");

        reader.QueryCount.Should().Be(1);
        var objectIds = reader.LastQuery.ObjectIds
            ?? throw new InvalidOperationException("policy query omitted object ids");
        objectIds.Should().Equal(42L, 43L);
        reader.LastQuery.Limit.Should().Be(2);
        var maskedFields = reader.LastQuery.EnforcedMaskedFields
            ?? throw new InvalidOperationException("policy query omitted the field mask");
        maskedFields.IsEmpty.Should().BeTrue();
        reader.LastQuery.EnforcedSqlFilter.Should().BeNull();
        var alice = OwnerEditPolicyFieldAccess.Decide(
            Policy,
            AttributeRuleEditEvent.Update,
            new EditPrincipal("alice", true, false),
            owners[42]);
        var bobOnAlice = OwnerEditPolicyFieldAccess.Decide(
            Policy,
            AttributeRuleEditEvent.Delete,
            new EditPrincipal("bob", true, false),
            owners[42]);
        var bob = OwnerEditPolicyFieldAccess.Decide(
            Policy,
            AttributeRuleEditEvent.Update,
            new EditPrincipal("bob", true, false),
            owners[43]);
        alice.IsAllowed.Should().BeTrue();
        bobOnAlice.IsAllowed.Should().BeFalse();
        bobOnAlice.Reason.Should().NotContain(Secret);
        bobOnAlice.Reason.Should().NotContain("alice");
        bob.IsAllowed.Should().BeTrue();
        reader.LastQuery.VersionContext.Should().BeNull();
    }

    [UnitTest]
    public async Task ReadOwnersAsync_NamedVersion_QueriesThatVersion()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var version = new VersionContext
        {
            VersionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            VersionName = "editor.branch"
        };

        await OwnerEditPolicyFieldAccess.ReadOwnersAsync(
            reader,
            layerId: 7,
            storageFeatureIds: [42L],
            ownerField: "owner",
            versionContext: version);

        reader.QueryCount.Should().Be(1);
        reader.LastQuery.VersionContext.Should().Be(version);
    }

    [UnitTest]
    public async Task ReadOwnersAsync_DefaultVersion_LeavesTheQueryUnversioned()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);

        await OwnerEditPolicyFieldAccess.ReadOwnersAsync(
            reader,
            layerId: 7,
            storageFeatureIds: [42L],
            ownerField: "owner",
            versionContext: VersionContext.Default);

        reader.LastQuery.VersionContext.Should().BeNull();
    }

    [UnitTest]
    public void ShouldPreserveStoredOwner_WhenTheUpdateOmitsTheMaskedField()
    {
        OwnerEditPolicyFieldAccess.ShouldPreserveStoredOwner(Policy, isPartialUpdate: true, ["name"])
            .Should().BeTrue();
        OwnerEditPolicyFieldAccess.ShouldPreserveStoredOwner(Policy, isPartialUpdate: true, ["name", "owner"])
            .Should().BeFalse();
        OwnerEditPolicyFieldAccess.ShouldPreserveStoredOwner(Policy, isPartialUpdate: false, ["name"])
            .Should().BeFalse();
        OwnerEditPolicyFieldAccess.ShouldPreserveStoredOwner(Policy with { Enabled = false }, true, ["name"])
            .Should().BeFalse();
    }

    [UnitTest]
    public async Task PrepareBatchAsync_NonOwnerUpdateAndDelete_AreDeniedBeforeAWrite()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var bob = new EditPrincipal("bob", IsAuthenticated: true, IsAdmin: false);
        var update = FeatureEditBatch.Create(updates: ImmutableArray.Create(Named(42)));
        var delete = FeatureEditBatch.Create(deletes: ImmutableArray.Create(42L));

        var updated = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, Policy, update, bob);
        var deleted = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, Policy, delete, bob);

        updated.IsAllowed.Should().BeFalse();
        deleted.IsAllowed.Should().BeFalse();
        updated.DenialReason.Should().Be("Feature is owned by another principal and cannot be modified under the owner-based edit policy.");
        deleted.DenialReason.Should().Be(updated.DenialReason);
        updated.DenialReason.Should().NotContain("alice");
        var denied = OwnerEditPolicyFieldAccess.DeniedEditResult(update, updated.DenialReason!);
        denied.WasRolledBack.Should().BeTrue();
        denied.UpdateResults.Should().ContainSingle();
        denied.UpdateResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        denied.UpdateResults[0].ErrorMessage.Should().Be(updated.DenialReason);
        reader.QueryCount.Should().Be(2);
    }

    [UnitTest]
    public async Task PrepareBatchAsync_Owner_SucceedsAndPreservesAnOmittedOwner()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var alice = new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false);
        var batch = FeatureEditBatch.Create(updates: ImmutableArray.Create(Named(42)));

        var prepared = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, Policy, batch, alice);

        prepared.IsAllowed.Should().BeTrue();
        prepared.Batch.Updates[0].Attributes["owner"].Should().Be("alice");
        prepared.Batch.Updates[0].Attributes["name"].Should().Be("visible");
    }

    [UnitTest]
    public async Task PrepareBatchAsync_Admin_BypassesWithoutReading()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var admin = new EditPrincipal("carol", IsAuthenticated: true, IsAdmin: true);
        var batch = FeatureEditBatch.Create(updates: ImmutableArray.Create(Named(42)), deletes: ImmutableArray.Create(43L));

        var prepared = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, Policy, batch, admin);

        prepared.IsAllowed.Should().BeTrue();
        prepared.Batch.Updates[0].Attributes.ContainsKey("owner").Should().BeFalse();
        reader.QueryCount.Should().Be(0);
    }

    [UnitTest]
    public async Task PrepareBatchAsync_Anonymous_IsDeniedWithoutReading()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var batch = FeatureEditBatch.Create(creates: ImmutableArray.Create(Named(0)));

        var prepared = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, Policy, batch, EditPrincipal.Anonymous);

        prepared.IsAllowed.Should().BeFalse();
        prepared.DenialReason.Should().Be("Anonymous edits are not permitted while an owner-based edit policy is active.");
        reader.QueryCount.Should().Be(0);
    }

    [UnitTest]
    public async Task PrepareBatchAsync_DisabledOrAbsentPolicy_KeepsTheBatchAndDoesNotRead()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var bob = new EditPrincipal("bob", IsAuthenticated: true, IsAdmin: false);
        var batch = FeatureEditBatch.Create(updates: ImmutableArray.Create(Named(42)), rollbackOnFailure: true);

        var disabled = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(
            reader, 7, Policy with { Enabled = false }, batch, bob);
        var absent = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, null, batch, bob);

        disabled.IsAllowed.Should().BeTrue();
        absent.IsAllowed.Should().BeTrue();
        disabled.Batch.Should().Be(batch);
        absent.Batch.Should().Be(batch);
        reader.QueryCount.Should().Be(0);
    }

    [UnitTest]
    public async Task PrepareBatchAsync_Insert_StampsOnlyWhenRequested()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var alice = new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false);
        var batch = FeatureEditBatch.Create(creates: ImmutableArray.Create(Named(0)));

        var stamped = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, Policy, batch, alice);
        var unstamped = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(
            reader, 7, Policy with { StampOwnerOnInsert = false }, batch, alice);

        stamped.IsAllowed.Should().BeTrue();
        stamped.Batch.Creates[0].Attributes["owner"].Should().Be("alice");
        unstamped.IsAllowed.Should().BeTrue();
        unstamped.Batch.Creates[0].Attributes.ContainsKey("owner").Should().BeFalse();
        reader.QueryCount.Should().Be(0);
    }

    [UnitTest]
    public async Task PrepareBatchAsync_OrderedOperations_DenyANonOwnerAndStampACreate()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var bob = new EditPrincipal("bob", IsAuthenticated: true, IsAdmin: false);
        var alice = new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false);
        var deniedBatch = FeatureEditBatch.Create(operations: ImmutableArray.Create(
            FeatureEditOperation.Update(Named(42)),
            FeatureEditOperation.Delete(43)));
        var stampedBatch = FeatureEditBatch.Create(operations: ImmutableArray.Create(FeatureEditOperation.Create(Named(0))));

        var denied = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, Policy, deniedBatch, bob);
        var stamped = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(reader, 7, Policy, stampedBatch, alice);

        denied.IsAllowed.Should().BeFalse();
        denied.DenialReason.Should().Be("Feature is owned by another principal and cannot be modified under the owner-based edit policy.");
        var result = OwnerEditPolicyFieldAccess.DeniedEditResult(deniedBatch, denied.DenialReason!);
        result.UpdateResults.Should().ContainSingle();
        result.DeleteResults.Should().ContainSingle();
        result.CreateResults.Should().BeEmpty();
        stamped.Batch.Operations[0].Feature!.Value.Attributes["owner"].Should().Be("alice");
    }

    [UnitTest]
    public async Task PrepareBatchAsync_ExplicitOwnerChange_IsDeniedAndAHiddenRowIsDenied()
    {
        var reader = new RecordingReader(RecordingReader.Mode.PolicyOwner);
        var alice = new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false);
        var changed = Feature.Create(42, null, ImmutableDictionary.CreateRange(new Dictionary<string, object?>
        {
            ["name"] = "visible",
            ["owner"] = "carol"
        }));

        var reassignment = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(
            reader, 7, Policy, FeatureEditBatch.Create(updates: ImmutableArray.Create(changed)), alice);
        var hidden = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(
            new RecordingReader(RecordingReader.Mode.Hidden),
            7,
            Policy,
            FeatureEditBatch.Create(deletes: ImmutableArray.Create(42L)),
            alice);

        reassignment.IsAllowed.Should().BeFalse();
        reassignment.DenialReason.Should().Be(OwnerEditPolicyFieldAccess.OwnerReassignmentDeniedReason);
        hidden.IsAllowed.Should().BeFalse();
        hidden.DenialReason.Should().Be("Edit not permitted.");
    }

    private static Feature Named(long id)
        => Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty.Add("name", "visible"));

    /// <summary>
    /// Returns the owner only when the query is the policy read. Any other query looks like a
    /// field-masked response: the owner is absent and an unrelated secret is present.
    /// </summary>
    private sealed class RecordingReader : IFeatureReader
    {
        private readonly Mode _mode;

        public RecordingReader(Mode mode) => _mode = mode;

        public int QueryCount { get; private set; }

        public FeatureQuery LastQuery { get; private set; }

        public Task<QueryResult<Feature>> QueryAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            LastQuery = query;
            if (_mode == Mode.Hidden || !IsPolicyQuery(query))
            {
                if (_mode == Mode.Hidden)
                {
                    return Task.FromResult(QueryResult<Feature>.Empty());
                }

                return Task.FromResult(QueryResult<Feature>.Create(
                    1,
                    ImmutableArray.Create(MaskedFeature(42))));
            }

            var features = ImmutableArray.CreateBuilder<Feature>();
            foreach (var id in query.ObjectIds!.Value)
            {
                features.Add(Feature.Create(
                    id,
                    null,
                    ImmutableDictionary.CreateRange(new Dictionary<string, object?>
                    {
                        ["owner"] = id == 43 ? "bob" : "alice",
                        ["secret"] = Secret
                    })));
            }

            return Task.FromResult(QueryResult<Feature>.Create(features.Count, features.ToImmutable()));
        }

        private static Feature MaskedFeature(long id)
            => Feature.Create(
                id,
                null,
                ImmutableDictionary.CreateRange(new Dictionary<string, object?>
                {
                    ["name"] = "visible",
                    ["secret"] = Secret
                }));

        public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(
            int layerId,
            string fieldName,
            TemporalPropertyType propertyType,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
            int layerId,
            FeatureQuery query,
            DateBinDefinition dateBin,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
            int layerId,
            FeatureQuery query,
            BinDefinition binDefinition,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
            int layerId,
            FeatureQuery query,
            H3AggregationQuery h3Query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static bool IsPolicyQuery(FeatureQuery query)
            => query.EnforcedMaskedFields is { IsDefault: false, IsEmpty: true }
                && query.EnforcedSqlFilter is null
                && query.ObjectIds is { } ids
                && ids.Length > 0
                && query.Limit == ids.Length
                && ids.All(static id => id is 42 or 43)
                && query.OutFields is { } fields
                && fields.Length == 1
                && string.Equals(fields[0], "owner", StringComparison.Ordinal);

        public enum Mode
        {
            PolicyOwner,
            Hidden
        }
    }
}
