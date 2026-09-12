// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.LoadTests;

/// <summary>
/// Verifies session deadlines independently of NBomber's request completion.
/// </summary>
public sealed class LoadRunDeadlineTests
{
    [UnitTest]
    public void TryComplete_LongBudget_DoesNotOverflowTaskWaitTimeout()
    {
        var expected = new object();
        Assert.True(LoadRunDeadline.TryComplete(() => expected, TimeSpan.FromDays(300), out var result));
        Assert.Same(expected, result);
    }

    [UnitTest]
    public void TryComplete_BlockedSession_RefusesPartialResultWithinBudget()
    {
        using var releaseSession = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        try
        {
            var completed = LoadRunDeadline.TryComplete(() =>
            {
                try
                {
                    // An intentionally unresponsive session models NBomber never returning,
                    // independently of its HTTP request and scenario-completion deadlines.
                    releaseSession.Wait();
                    return new object();
                }
                finally
                {
                    finished.Set();
                }
            }, TimeSpan.FromMilliseconds(50), out var result);

            Assert.False(completed);
            Assert.Null(result);
        }
        finally
        {
            releaseSession.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
        }
    }
}
