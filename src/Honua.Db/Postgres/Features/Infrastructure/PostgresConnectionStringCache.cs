// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Db.Postgres.Features.Infrastructure;

internal sealed class PostgresConnectionStringCache
{
    public Task<string> ResolvedConnectionStringTask { get; }

    public PostgresConnectionStringCache(string rawConnectionString, IConnectionSecretResolver? resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawConnectionString);
        ResolvedConnectionStringTask = ResolveAsync(rawConnectionString, resolver);
    }

    private static async Task<string> ResolveAsync(string raw, IConnectionSecretResolver? resolver)
    {
        if (resolver == null || !resolver.CanResolve(raw))
        {
            return raw;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await resolver.ResolveSecretAsync(raw, cts.Token).ConfigureAwait(false) ?? raw;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Timed out after 30 s while resolving the Postgres connection string secret.", null);
        }
    }
}
