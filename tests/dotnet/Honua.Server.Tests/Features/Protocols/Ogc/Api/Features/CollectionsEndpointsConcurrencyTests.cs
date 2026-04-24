// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Protocol(TestProtocols.TestQuality)]
public sealed class CollectionsEndpointsConcurrencyTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ProjectWithLimitedConcurrencyAsync_BoundsParallelismAndPreservesOrder()
    {
        var items = Enumerable.Range(0, 20).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new object();
        var active = 0;
        var maxObserved = 0;

        var projectionTask = CollectionsEndpoints.ProjectWithLimitedConcurrencyAsync(
            items,
            async (item, ct) =>
            {
                var current = Interlocked.Increment(ref active);
                lock (sync)
                {
                    if (current > maxObserved)
                    {
                        maxObserved = current;
                    }

                    if (current == CollectionsEndpoints.MaxCollectionProjectionConcurrency)
                    {
                        started.TrySetResult();
                    }
                }

                await gate.Task.WaitAsync(ct);
                Interlocked.Decrement(ref active);
                return item.ToString(CultureInfo.InvariantCulture);
            },
            CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetResult();

        var results = await projectionTask;

        results.Should().Equal(items.Select(item => item.ToString(CultureInfo.InvariantCulture)));
        maxObserved.Should().BeLessOrEqualTo(CollectionsEndpoints.MaxCollectionProjectionConcurrency);
    }
}
