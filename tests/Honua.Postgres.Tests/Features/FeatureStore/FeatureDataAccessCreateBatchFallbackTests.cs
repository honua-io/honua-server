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
}
