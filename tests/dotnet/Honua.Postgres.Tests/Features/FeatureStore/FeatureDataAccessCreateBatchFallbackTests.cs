// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;

namespace Honua.Postgres.Tests.Features.FeatureStore;

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
    // ─── BH7-020 regression ──────────────────────────────────────────────────────

    /// <summary>
    /// BH7-020 regression: OperationCanceledException must propagate from
    /// <see cref="FeatureDataAccess.ExecuteAdaptiveNonTransactionalCreateBatchAsync"/> when
    /// the cancellation token is signalled, not be swallowed into a generic Failure result.
    /// </summary>
    [Fact]
    public async Task ExecuteAdaptiveNonTransactionalCreateBatchAsync_CancelledToken_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel

        var act = async () => await FeatureDataAccess.ExecuteAdaptiveNonTransactionalCreateBatchAsync(
            ImmutableArray.Create(1),
            (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(ImmutableArray.Create(10L));
            },
            (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(((long?)10L, EditOperationResult.Success(10)));
            },
            static (_, id) => EditOperationResult.Success(id),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "BH7-020: OperationCanceledException must propagate, not be swallowed");
    }

    // ─── BH7-021 regression ──────────────────────────────────────────────────────

    /// <summary>
    /// BH7-021 regression: when a batch fails on a subset of features, the adaptive split
    /// path must return partial results that accurately reflect which rows committed rather
    /// than reporting all operations as failed.
    /// </summary>
    [Fact]
    public async Task ExecuteAdaptiveNonTransactionalCreateBatchAsync_PartialFailure_ReportsCommittedRowsAccurately()
    {
        var result = await FeatureDataAccess.ExecuteAdaptiveNonTransactionalCreateBatchAsync(
            ImmutableArray.Create(1, 2, 3),
            (batch, _) =>
            {
                if (batch.Contains(3))
                {
                    throw new InvalidOperationException("batch with item 3 fails");
                }

                return Task.FromResult(batch.Select(static i => (long)i * 10).ToImmutableArray());
            },
            (item, _) =>
            {
                if (item == 3)
                {
                    return Task.FromResult(((long?)null, EditOperationResult.Failure("Item 3 rejected.")));
                }

                return Task.FromResult(((long?)item * 10,
                    EditOperationResult.Success(item * 10L, item.ToString(CultureInfo.InvariantCulture))));
            },
            static (item, id) => EditOperationResult.Success(id, item.ToString(CultureInfo.InvariantCulture)),
            CancellationToken.None);

        result.createdIds.Should().BeEquivalentTo([10L, 20L],
            "BH7-021: committed rows must appear in createdIds, not all reported as globally failed");
        result.results.Should().HaveCount(3);
        result.results[0].IsSuccess.Should().BeTrue("item 1 committed");
        result.results[1].IsSuccess.Should().BeTrue("item 2 committed");
        result.results[2].IsSuccess.Should().BeFalse("item 3 was rejected");
        result.results[2].ErrorMessage.Should().Be("Item 3 rejected.");
    }
}
