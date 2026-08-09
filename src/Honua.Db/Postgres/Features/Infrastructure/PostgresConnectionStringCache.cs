// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Singleton that resolves (and caches) the Postgres connection string exactly once,
/// eliminating the sync-over-async pattern on the per-request scoped factory path
/// (PA-077).
/// </summary>
/// <remarks>
/// <para>
/// The previous design called
/// <c>resolver.ResolveSecretAsync(...).GetAwaiter().GetResult()</c> inside every
/// scoped DI factory lambda — once per request for each catalog that needed the
/// connection string. On cloud deployments this blocked a thread-pool thread for
/// every AWS Secrets Manager / Azure Key Vault network call, risking thread-pool
/// exhaustion under load.
/// </para>
/// <para>
/// This singleton starts the async resolution immediately upon construction (which
/// happens at DI registration time, before the server begins serving requests) and
/// exposes the result as <see cref="ResolvedConnectionStringTask"/>. Scoped
/// consumers <c>await</c> the task directly; by the time the first HTTP request
/// arrives the task is already completed, so the await is effectively free
/// (zero I/O, no thread blocking). If resolution is still in-flight (edge case at
/// startup) the await yields properly without blocking any thread.
/// </para>
/// <para>
/// A bounded 30-second <see cref="CancellationTokenSource"/> guards against a hung
/// secret provider. <c>CancellationToken.None</c> is no longer passed to the
/// resolver from this path.
/// </para>
/// </remarks>
internal sealed class PostgresConnectionStringCache
{
    /// <summary>
    /// A <see cref="Task{TResult}"/> that resolves to the fully-substituted Postgres
    /// connection string. Completed at most once; await freely — it is thread-safe.
    /// </summary>
    public Task<string> ResolvedConnectionStringTask { get; }

    /// <summary>
    /// Initialises the cache and starts background async secret resolution.
    /// </summary>
    /// <param name="rawConnectionString">
    /// The raw connection string from configuration (may contain a secret reference).
    /// </param>
    /// <param name="resolver">
    /// Optional secret resolver. When <see langword="null"/> the raw string is used as-is.
    /// </param>
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
