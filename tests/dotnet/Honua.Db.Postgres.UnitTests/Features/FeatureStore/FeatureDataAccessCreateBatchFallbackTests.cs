// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class FeatureDataAccessCreateBatchFallbackTests
{
    [Fact]
    public async Task ExecuteAdaptiveNonTransactionalCreateBatchAsync_SplitsFailingBatchUntilSubBatchesSucceed()
    {
        var attemptedBatchSizes = new List<int>();

        var result = await FeatureDataAccess.ExecuteAdaptiveNonTransactionalCreateBatchAsync(
            ImmutableArray.Create(1, 2, 3, 4),
            (batch, _) =>
            {
                attemptedBatchSizes.Add(batch.Length);
                if (batch.Length > 2)
                {
                    throw new InvalidOperationException("simulated batch failure");
                }

                return Task.FromResult(batch.Select(static item => (long)item * 10).ToImmutableArray());
            },
            (_, _) => throw new Xunit.Sdk.XunitException("single-item fallback should not be used"),
            static (item, createdId) => EditOperationResult.Success(createdId, item.ToString(CultureInfo.InvariantCulture)),
            CancellationToken.None);

        result.createdIds.Should().Equal([10L, 20L, 30L, 40L]);
        result.results.Should().OnlyContain(static outcome => outcome.IsSuccess);
        attemptedBatchSizes.Should().Equal([4, 2, 2]);
    }

    [Fact]
    public async Task ExecuteAdaptiveNonTransactionalCreateBatchAsync_IsolatesSingleFailingFeature()
    {
        var singleCreateAttempts = new List<int>();

        var result = await FeatureDataAccess.ExecuteAdaptiveNonTransactionalCreateBatchAsync(
            ImmutableArray.Create(1, 2, 3, 4),
            (batch, _) =>
            {
                if (batch.Contains(3) && batch.Length > 1)
                {
                    throw new InvalidOperationException("simulated mixed batch failure");
                }

                return Task.FromResult(batch.Select(static item => (long)item * 10).ToImmutableArray());
            },
            (item, _) =>
            {
                singleCreateAttempts.Add(item);
                return Task.FromResult(item == 3
                    ? ((long?)null, EditOperationResult.Failure("Invalid feature data."))
                    : ((long?)item * 10, EditOperationResult.Success(item * 10, item.ToString(CultureInfo.InvariantCulture))));
            },
            static (item, createdId) => EditOperationResult.Success(createdId, item.ToString(CultureInfo.InvariantCulture)),
            CancellationToken.None);

        result.createdIds.Should().Equal([10L, 20L, 40L]);
        result.results.Should().HaveCount(4);
        result.results[0].IsSuccess.Should().BeTrue();
        result.results[1].IsSuccess.Should().BeTrue();
        result.results[2].IsSuccess.Should().BeFalse();
        result.results[2].ErrorMessage.Should().Be("Invalid feature data.");
        result.results[3].IsSuccess.Should().BeTrue();
        singleCreateAttempts.Should().Equal([3, 4]);
    }

    [Fact]
    public async Task ExecuteAdaptiveNonTransactionalCreateBatchAsync_CancelledBatchInsert_DoesNotSplit()
    {
        using var cts = new CancellationTokenSource();

        var act = () => FeatureDataAccess.ExecuteAdaptiveNonTransactionalCreateBatchAsync(
            ImmutableArray.Create(1, 2, 3, 4),
            (_, ct) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(ct);
            },
            (_, _) => throw new Xunit.Sdk.XunitException("a cancelled batch insert must not be split"),
            static (item, createdId) => EditOperationResult.Success(createdId, item.ToString(CultureInfo.InvariantCulture)),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAdaptiveNonTransactionalCreateBatchAsync_CancelledRightHalf_KeepsCommittedLeftPrefix()
    {
        using var cts = new CancellationTokenSource();

        var result = await FeatureDataAccess.ExecuteAdaptiveNonTransactionalCreateBatchAsync(
            ImmutableArray.Create(1, 2, 3, 4),
            (batch, _) =>
            {
                if (batch.Length > 2)
                {
                    throw new InvalidOperationException("simulated batch failure");
                }

                if (batch.Contains(3))
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return Task.FromResult(batch.Select(static item => (long)item * 10).ToImmutableArray());
            },
            (_, _) => throw new Xunit.Sdk.XunitException("single-item fallback should not be used"),
            static (item, createdId) => EditOperationResult.Success(createdId, item.ToString(CultureInfo.InvariantCulture)),
            cts.Token);

        result.createdIds.Should().Equal([10L, 20L]);
        result.results.Should().HaveCount(2);
        result.results.Should().OnlyContain(static outcome => outcome.IsSuccess);
        result.results.Should().OnlyContain(static outcome => !outcome.IsCommitOutcomeUnknown);
    }

    [Fact]
    public async Task ExecuteAdaptiveNonTransactionalCreateBatchAsync_CancelledSingleRow_StopsTheRemainder()
    {
        using var cts = new CancellationTokenSource();
        var singleCreateAttempts = new List<int>();

        var result = await FeatureDataAccess.ExecuteAdaptiveNonTransactionalCreateBatchAsync(
            ImmutableArray.Create(1, 2, 3, 4),
            (_, _) => throw new InvalidOperationException("simulated batch failure"),
            (item, ct) =>
            {
                singleCreateAttempts.Add(item);
                if (item == 2)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(ct);
                }

                return Task.FromResult(((long?)item * 10, EditOperationResult.Success(
                    item * 10L,
                    item.ToString(CultureInfo.InvariantCulture))));
            },
            static (item, createdId) => EditOperationResult.Success(createdId, item.ToString(CultureInfo.InvariantCulture)),
            cts.Token);

        result.createdIds.Should().Equal([10L]);
        result.results.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        singleCreateAttempts.Should().Equal([1, 2]);
    }

    [Fact]
    public void CancelledAutocommitOrderedResult_NamesCommittedRowsAndMarksTheTailUnknown()
    {
        var batch = FeatureEditBatch.Create(
            operations: ImmutableArray.Create(
                FeatureEditOperation.Create(Feature.Create(0, null)),
                FeatureEditOperation.Update(Feature.Create(7, null)),
                FeatureEditOperation.Delete(9)));

        var result = FeatureDataAccess.CancelledAutocommitOrderedResult(
            batch,
            firstUnrecordedIndex: 1,
            createdIds: ImmutableArray.Create(42L),
            createResults: ImmutableArray.Create(EditOperationResult.Success(42)),
            updateResults: ImmutableArray<EditOperationResult>.Empty,
            deleteResults: ImmutableArray<EditOperationResult>.Empty);

        result.WasRolledBack.Should().BeFalse();
        result.MayHaveCommitted.Should().BeTrue();
        result.CreatedCount.Should().Be(1);
        result.CreatedIds.Should().Equal([42L]);
        result.CreateResults.Should().ContainSingle().Which.ObjectId.Should().Be(42);
        result.UpdateResults.Should().ContainSingle();
        result.UpdateResults[0].IsSuccess.Should().BeFalse();
        result.UpdateResults[0].IsCommitOutcomeUnknown.Should().BeTrue();
        result.UpdateResults[0].ObjectId.Should().Be(7);
        result.UpdateResults[0].ErrorMessage.Should().Be("Edit cancelled.");
        result.DeleteResults.Should().ContainSingle();
        result.DeleteResults[0].IsCommitOutcomeUnknown.Should().BeTrue();
        result.DeleteResults[0].ObjectId.Should().Be(9);
    }
}
