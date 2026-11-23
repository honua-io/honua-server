// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Configuration;

public sealed class RequestFanOutTests
{
    [UnitTest]
    public async Task SelectAsync_ManyItems_RunsAtMostMaxConcurrencyAndKeepsOrder()
    {
        var items = Enumerable.Range(0, 5 * RequestFanOut.MaxConcurrency).ToArray();
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new Lock();
        var active = 0;
        var maxActive = 0;

        var selection = RequestFanOut.SelectAsync(items, async (item, cancellationToken) =>
        {
            lock (sync)
            {
                maxActive = Math.Max(maxActive, ++active);
                if (active == RequestFanOut.MaxConcurrency)
                {
                    saturated.TrySetResult();
                }
            }

            await release.Task.WaitAsync(cancellationToken);
            lock (sync)
            {
                active--;
            }

            return item * 2;
        }, CancellationToken.None);

        await saturated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        (await selection).Should().Equal(items.Select(item => item * 2));
        maxActive.Should().Be(RequestFanOut.MaxConcurrency);
    }

    [UnitTest]
    public async Task SelectAsync_ProjectorFails_PropagatesFailure()
    {
        var selection = () => RequestFanOut.SelectAsync<int, int>(
            [1, 2, 3],
            (item, _) => item == 2 ? Task.FromException<int>(new InvalidOperationException("read failed")) : Task.FromResult(item),
            CancellationToken.None);

        await selection.Should().ThrowAsync<InvalidOperationException>().WithMessage("read failed");
    }
}
