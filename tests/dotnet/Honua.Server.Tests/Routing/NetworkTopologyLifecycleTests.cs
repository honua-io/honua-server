// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Provider-neutral topology generation lifecycle contract tests.
/// </summary>
public sealed class NetworkTopologyLifecycleTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NetworkTopologyGenerationState.Draft, NetworkTopologyGenerationState.Dirty)]
    [InlineData(NetworkTopologyGenerationState.Dirty, NetworkTopologyGenerationState.Building)]
    [InlineData(NetworkTopologyGenerationState.Building, NetworkTopologyGenerationState.Ready)]
    [InlineData(NetworkTopologyGenerationState.Building, NetworkTopologyGenerationState.Failed)]
    [InlineData(NetworkTopologyGenerationState.Ready, NetworkTopologyGenerationState.Active)]
    [InlineData(NetworkTopologyGenerationState.Ready, NetworkTopologyGenerationState.Failed)]
    [InlineData(NetworkTopologyGenerationState.Active, NetworkTopologyGenerationState.Retired)]
    [InlineData(NetworkTopologyGenerationState.Retired, NetworkTopologyGenerationState.Active)]
    [InlineData(NetworkTopologyGenerationState.Failed, NetworkTopologyGenerationState.Dirty)]
    [InlineData(NetworkTopologyGenerationState.Failed, NetworkTopologyGenerationState.Retired)]
    public void CanTransition_LegalTransition_ReturnsTrue(
        NetworkTopologyGenerationState current,
        NetworkTopologyGenerationState target)
    {
        Assert.True(NetworkTopologyLifecycle.CanTransition(current, target));
    }

    [Theory]
    [InlineData(NetworkTopologyGenerationState.Active, NetworkTopologyGenerationState.Dirty)]
    [InlineData(NetworkTopologyGenerationState.Active, NetworkTopologyGenerationState.Building)]
    [InlineData(NetworkTopologyGenerationState.Dirty, NetworkTopologyGenerationState.Active)]
    [InlineData(NetworkTopologyGenerationState.Building, NetworkTopologyGenerationState.Active)]
    [InlineData(NetworkTopologyGenerationState.Retired, NetworkTopologyGenerationState.Dirty)]
    [InlineData(NetworkTopologyGenerationState.Retired, NetworkTopologyGenerationState.Building)]
    [InlineData(NetworkTopologyGenerationState.Ready, NetworkTopologyGenerationState.Dirty)]
    public void CanTransition_UnsafeTransition_ReturnsFalse(
        NetworkTopologyGenerationState current,
        NetworkTopologyGenerationState target)
    {
        Assert.False(NetworkTopologyLifecycle.CanTransition(current, target));
    }

    [Fact]
    public void TryTransition_MatchingVersion_IncrementsVersionAndActivatesGeneration()
    {
        var generation = CreateGeneration(NetworkTopologyGenerationState.Ready, rowVersion: 7);

        var succeeded = NetworkTopologyLifecycle.TryTransition(
            generation,
            expectedRowVersion: 7,
            NetworkTopologyGenerationState.Active,
            _now,
            failureCode: null,
            out var updated,
            out var failure);

        Assert.True(succeeded);
        Assert.Equal(NetworkTopologyTransitionFailure.None, failure);
        Assert.Equal(8, updated.RowVersion);
        Assert.Equal(NetworkTopologyGenerationState.Active, updated.State);
        Assert.Equal(_now, updated.UpdatedAt);
        Assert.Equal(_now, updated.ActivatedAt);
    }

    [Fact]
    public void TryTransition_StaleVersion_ReturnsConflictWithoutMutation()
    {
        var generation = CreateGeneration(NetworkTopologyGenerationState.Dirty, rowVersion: 4);

        var succeeded = NetworkTopologyLifecycle.TryTransition(
            generation,
            expectedRowVersion: 3,
            NetworkTopologyGenerationState.Building,
            _now,
            failureCode: null,
            out var updated,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(NetworkTopologyTransitionFailure.StaleRowVersion, failure);
        Assert.Same(generation, updated);
    }

    [Fact]
    public void TryTransition_FailedWithUnsafeCode_ReturnsStableValidationFailure()
    {
        var generation = CreateGeneration(NetworkTopologyGenerationState.Building, rowVersion: 2);

        var succeeded = NetworkTopologyLifecycle.TryTransition(
            generation,
            expectedRowVersion: 2,
            NetworkTopologyGenerationState.Failed,
            _now,
            "SQL failed in public.ways",
            out var updated,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(NetworkTopologyTransitionFailure.InvalidFailureCode, failure);
        Assert.Same(generation, updated);
    }

    [Fact]
    public void TryTransition_FailedWithStableCode_StoresSanitizedCode()
    {
        var generation = CreateGeneration(NetworkTopologyGenerationState.Building, rowVersion: 2);

        var succeeded = NetworkTopologyLifecycle.TryTransition(
            generation,
            expectedRowVersion: 2,
            NetworkTopologyGenerationState.Failed,
            _now,
            "routing.topology.build_failed",
            out var updated,
            out var failure);

        Assert.True(succeeded);
        Assert.Equal(NetworkTopologyTransitionFailure.None, failure);
        Assert.Equal("routing.topology.build_failed", updated.FailureCode);
        Assert.Null(updated.ActivatedAt);
    }

    [Theory]
    [InlineData(NetworkTopologyGenerationState.Draft)]
    [InlineData(NetworkTopologyGenerationState.Dirty)]
    public void TryApplyContentEdit_DraftOrDirty_TransitionsToDirtyAndBumpsRevisionAndVersion(
        NetworkTopologyGenerationState startingState)
    {
        var generation = CreateGeneration(startingState, rowVersion: 3);

        var succeeded = NetworkTopologyLifecycle.TryApplyContentEdit(
            generation,
            expectedRowVersion: 3,
            _now,
            out var updated,
            out var rejection);

        Assert.True(succeeded);
        Assert.Equal(NetworkTopologyEditRejection.None, rejection);
        Assert.Equal(NetworkTopologyGenerationState.Dirty, updated.State);
        Assert.Equal(4, updated.RowVersion);
        Assert.Equal(10, updated.SourceRevision);
        Assert.Equal(_now, updated.UpdatedAt);
    }

    [Fact]
    public void TryApplyContentEdit_StaleVersion_ReturnsConflictWithoutMutation()
    {
        var generation = CreateGeneration(NetworkTopologyGenerationState.Draft, rowVersion: 5);

        var succeeded = NetworkTopologyLifecycle.TryApplyContentEdit(
            generation,
            expectedRowVersion: 4,
            _now,
            out var updated,
            out var rejection);

        Assert.False(succeeded);
        Assert.Equal(NetworkTopologyEditRejection.StaleRowVersion, rejection);
        Assert.Same(generation, updated);
    }

    [Theory]
    [InlineData(NetworkTopologyGenerationState.Building)]
    [InlineData(NetworkTopologyGenerationState.Ready)]
    [InlineData(NetworkTopologyGenerationState.Active)]
    [InlineData(NetworkTopologyGenerationState.Failed)]
    [InlineData(NetworkTopologyGenerationState.Retired)]
    public void TryApplyContentEdit_NonEditableState_ReturnsRejectionWithoutMutation(
        NetworkTopologyGenerationState nonEditableState)
    {
        var generation = CreateGeneration(nonEditableState, rowVersion: 1);

        var succeeded = NetworkTopologyLifecycle.TryApplyContentEdit(
            generation,
            expectedRowVersion: 1,
            _now,
            out var updated,
            out var rejection);

        Assert.False(succeeded);
        Assert.Equal(NetworkTopologyEditRejection.GenerationNotEditable, rejection);
        Assert.Same(generation, updated);
    }

    private static NetworkTopologyGeneration CreateGeneration(
        NetworkTopologyGenerationState state,
        long rowVersion) => new(
            DatasetId: "default",
            Generation: 2,
            SourceRevision: 9,
            State: state,
            RowVersion: rowVersion,
            Srid: 4326,
            CreatedAt: _now.AddMinutes(-5),
            UpdatedAt: _now.AddMinutes(-2),
            ActivatedAt: null,
            FailureCode: null);
}
