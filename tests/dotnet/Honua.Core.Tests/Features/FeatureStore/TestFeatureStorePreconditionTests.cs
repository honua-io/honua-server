// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Core.Tests.Features.FeatureStore;

/// <summary>
/// Verifies that the in-memory feature writer honors <see cref="FeatureEditBatch.Preconditions"/>
/// with compare-and-write semantics equivalent to a SQL writer's
/// "UPDATE ... WHERE state-token matches; affected rows == 0 means precondition failed":
/// a stale expected-state token rejects the operation with a typed
/// <see cref="EditOperationResult.PreconditionFailed"/> result and leaves the stored
/// feature untouched, while a current token lets the write proceed.
/// </summary>
public sealed class TestFeatureStorePreconditionTests
{
    private const int LayerId = 0;

    private static FeatureEditPrecondition PreconditionFor(Feature snapshot) => new()
    {
        ObjectId = snapshot.Id,
        ExpectedStateToken = FeatureStateToken.Compute(snapshot)
    };

    private static Feature WithName(Feature feature, string name)
        => Feature.Create(feature.Id, feature.Geometry, feature.Attributes.SetItem("name", name));

    [UnitTest]
    public async Task ApplyEdits_UpdateWithCurrentToken_Succeeds()
    {
        using var store = new TestFeatureStore();
        var snapshot = (await store.GetAsync(LayerId, 1))!.Value;

        var result = await store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            updates: ImmutableArray.Create(WithName(snapshot, "renamed")),
            preconditions: ImmutableArray.Create(PreconditionFor(snapshot))));

        result.UpdatedCount.Should().Be(1);
        result.UpdateResults.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();

        var persisted = (await store.GetAsync(LayerId, 1))!.Value;
        persisted.Attributes["name"].Should().Be("renamed");
    }

    [UnitTest]
    public async Task ApplyEdits_UpdateWithStaleToken_FailsTypedAndDoesNotWrite()
    {
        using var store = new TestFeatureStore();
        var snapshot = (await store.GetAsync(LayerId, 1))!.Value;

        // Concurrent writer commits between the caller's snapshot read and its edit.
        await store.UpdateAsync(LayerId, WithName(snapshot, "concurrent"));

        var result = await store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            updates: ImmutableArray.Create(WithName(snapshot, "stale-write")),
            preconditions: ImmutableArray.Create(PreconditionFor(snapshot))));

        // Affected-rows semantics: the conditional write matched zero rows.
        result.UpdatedCount.Should().Be(0);
        var failure = result.UpdateResults.Should().ContainSingle().Subject;
        failure.IsSuccess.Should().BeFalse();
        failure.IsPreconditionFailure.Should().BeTrue();
        failure.ErrorCode.Should().Be(EditOperationResult.PreconditionFailedErrorCode);
        failure.ObjectId.Should().Be(1);

        // The stale image must not have overwritten the concurrent writer's commit.
        var persisted = (await store.GetAsync(LayerId, 1))!.Value;
        persisted.Attributes["name"].Should().Be("concurrent");
    }

    [UnitTest]
    public async Task ApplyEdits_DeleteWithStaleToken_FailsTypedAndKeepsFeature()
    {
        using var store = new TestFeatureStore();
        var snapshot = (await store.GetAsync(LayerId, 2))!.Value;

        await store.UpdateAsync(LayerId, WithName(snapshot, "concurrent"));

        var result = await store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            deletes: ImmutableArray.Create(2L),
            preconditions: ImmutableArray.Create(PreconditionFor(snapshot))));

        result.DeletedCount.Should().Be(0);
        var failure = result.DeleteResults.Should().ContainSingle().Subject;
        failure.IsPreconditionFailure.Should().BeTrue();
        failure.ObjectId.Should().Be(2);

        (await store.GetAsync(LayerId, 2)).Should().NotBeNull();
    }

    [UnitTest]
    public async Task ApplyEdits_DeleteWithCurrentToken_Succeeds()
    {
        using var store = new TestFeatureStore();
        var snapshot = (await store.GetAsync(LayerId, 2))!.Value;

        var result = await store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            deletes: ImmutableArray.Create(2L),
            preconditions: ImmutableArray.Create(PreconditionFor(snapshot))));

        result.DeletedCount.Should().Be(1);
        (await store.GetAsync(LayerId, 2)).Should().BeNull();
    }

    [UnitTest]
    public async Task ApplyEdits_OrderedUpdateWithStaleToken_FailsTyped()
    {
        using var store = new TestFeatureStore();
        var snapshot = (await store.GetAsync(LayerId, 1))!.Value;

        await store.UpdateAsync(LayerId, WithName(snapshot, "concurrent"));

        var result = await store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            operations: ImmutableArray.Create(FeatureEditOperation.Update(WithName(snapshot, "stale-write"))),
            preconditions: ImmutableArray.Create(PreconditionFor(snapshot))));

        var failure = result.UpdateResults.Should().ContainSingle().Subject;
        failure.IsPreconditionFailure.Should().BeTrue();

        var persisted = (await store.GetAsync(LayerId, 1))!.Value;
        persisted.Attributes["name"].Should().Be("concurrent");
    }

    [UnitTest]
    public async Task ApplyEdits_StalePreconditionWithRollback_RollsBackWholeBatch()
    {
        using var store = new TestFeatureStore();
        var staleSnapshot = (await store.GetAsync(LayerId, 1))!.Value;
        var otherSnapshot = (await store.GetAsync(LayerId, 3))!.Value;

        await store.UpdateAsync(LayerId, WithName(staleSnapshot, "concurrent"));

        var result = await store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            updates: ImmutableArray.Create(
                WithName(otherSnapshot, "other-renamed"),
                WithName(staleSnapshot, "stale-write")),
            rollbackOnFailure: true,
            preconditions: ImmutableArray.Create(PreconditionFor(staleSnapshot))));

        result.WasRolledBack.Should().BeTrue();
        result.UpdateResults.Should().Contain(r => r.IsPreconditionFailure);

        // The unconditional sibling update must have been rolled back too.
        var untouched = (await store.GetAsync(LayerId, 3))!.Value;
        untouched.Attributes["name"].Should().Be("Third Feature");
    }

    [UnitTest]
    public async Task ApplyEdits_ObjectIdsWithoutPrecondition_KeepLastWriteWins()
    {
        using var store = new TestFeatureStore();
        var snapshot = (await store.GetAsync(LayerId, 1))!.Value;

        await store.UpdateAsync(LayerId, WithName(snapshot, "concurrent"));

        // No precondition attached: the stale image wins (documented HTTP semantics
        // when the client supplies no If-Match).
        var result = await store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            updates: ImmutableArray.Create(WithName(snapshot, "stale-write"))));

        result.UpdatedCount.Should().Be(1);
        var persisted = (await store.GetAsync(LayerId, 1))!.Value;
        persisted.Attributes["name"].Should().Be("stale-write");
    }
}
