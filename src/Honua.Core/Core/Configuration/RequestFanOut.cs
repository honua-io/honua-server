// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Configuration;

// One request that reads many layers runs at most MaxConcurrency of those reads at a time, so the
// number of published layers does not decide how many database connections a single request holds.
public static class RequestFanOut
{
    public const int MaxConcurrency = 8;

    // Results keep the order of the source items.
    public static async Task<TResult[]> SelectAsync<TSource, TResult>(
        IReadOnlyList<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> projector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projector);

        if (source.Count == 0)
        {
            return [];
        }

        var results = new TResult[source.Count];
        var nextIndex = -1;

        async Task RunWorkerAsync()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var index = Interlocked.Increment(ref nextIndex);
                if (index >= source.Count)
                {
                    return;
                }

                results[index] = await projector(source[index], cancellationToken).ConfigureAwait(false);
            }
        }

        var workers = Enumerable.Range(0, Math.Min(MaxConcurrency, source.Count)).Select(_ => RunWorkerAsync());
        await Task.WhenAll(workers).ConfigureAwait(false);
        return results;
    }
}
