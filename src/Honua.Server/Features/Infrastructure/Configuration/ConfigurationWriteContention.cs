// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Configuration;

// Optimistic configuration writes retry when another writer committed first. Without a pause, writers on one
// document retry in lockstep and the last of many can lose every round.
internal static class ConfigurationWriteContention
{
    internal const int MaxAttempts = 16;

    // A short random pause that grows with each lost round spreads the retries out.
    internal static Task PauseAsync(int attempt, CancellationToken token)
        => Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1, (4 * (attempt + 1)) + 1)), token);
}

// The write lost every retry to other writers; the caller may try again.
internal sealed class ConfigurationWriteConflictException : InvalidOperationException
{
    public ConfigurationWriteConflictException()
        : base("Concurrent configuration updates exceeded the retry limit.")
    {
    }
}
