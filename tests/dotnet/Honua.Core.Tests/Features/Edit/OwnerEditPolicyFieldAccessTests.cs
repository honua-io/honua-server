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
