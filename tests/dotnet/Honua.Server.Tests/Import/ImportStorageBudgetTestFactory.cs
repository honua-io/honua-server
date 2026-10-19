// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Import.FileImport.Resumable;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

// These isolated ledger/component fixtures explicitly supply an empty census.
// They are not evidence of production physical census or journal initialization.
internal static class ImportStorageBudgetTestFactory
{
    internal const string Epoch = "cedd506b83b542d2a6219bc5d2265634";

    internal static RedisImportStorageBudget Bind(IConnectionMultiplexer connection, string domain, ImportStorageBudgetLimits limits)
        => new(connection, domain, limits, Epoch);

    internal static async Task<RedisImportStorageBudget> CreateAsync(IConnectionMultiplexer connection, string domain, ImportStorageBudgetLimits limits)
    {
        var budget = Bind(connection, domain, limits);
        var previous = await budget.ReadStoredEpochAsync();
        if (previous is null) await InitializeEmptyAsync(budget);
        else if (previous != Epoch) throw new InvalidDataException("The isolated test ledger belongs to a different epoch.");
        return budget;
    }

    internal static Task InitializeEmptyAsync(RedisImportStorageBudget budget)
        => budget.RebuildFromCensusAsync(null, Empty());

    private static async IAsyncEnumerable<ImportStorageReservation> Empty()
    {
        await Task.Yield();
        yield break;
    }
}
