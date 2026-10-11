// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

namespace Honua.Server.Startup;

/// <summary>
/// Creates the bootstrap-only AWS Secrets Manager resolver that <c>Program.cs</c> uses before the
/// DI container exists: the settings document, the security-setting snapshots, the Redis
/// connection string and the key-ring certificate. Every one of those steps uses the SAME
/// AOT-safe <see cref="AwsSecretsManagerResolver"/> (raw HTTP + SigV4, no AWSSDK dependency) the
/// DI-registered Postgres path uses, so a secret name resolves in the function's own account and
/// region and an ARN resolves anywhere the role can read.
/// </summary>
internal static class BootstrapSecretResolver
{
    private static readonly AsyncLocal<IConnectionSecretResolver?> _testOverride = new();

    /// <summary>
    /// Creates a lease over a bootstrap resolver. The caller disposes the lease, which disposes the
    /// HTTP clients and logger factory it owns.
    /// </summary>
    public static Lease Create()
    {
        var overrideResolver = _testOverride.Value;
        if (overrideResolver is not null)
        {
            return new Lease(overrideResolver, owned: null);
        }

        var loggerFactory = LoggerFactory.Create(static builder => builder.AddConsole());
        var secretsClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var metadataClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var resolver = new AwsSecretsManagerResolver(
            secretsClient,
            metadataClient,
            loggerFactory.CreateLogger<AwsSecretsManagerResolver>());
        return new Lease(resolver, [resolver, metadataClient, secretsClient, loggerFactory]);
    }

    /// <summary>
    /// Routes every bootstrap resolution started from the current async flow through
    /// <paramref name="resolver"/> until the returned scope is disposed. Test-only: it lets a
    /// composition test boot the real <c>Program</c> against a stub Secrets Manager. The scope is
    /// an <see cref="AsyncLocal{T}"/>, so concurrently booting hosts never observe each other's stub.
    /// </summary>
    internal static IDisposable UseForTesting(IConnectionSecretResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var previous = _testOverride.Value;
        _testOverride.Value = resolver;
        return new OverrideScope(previous);
    }

    /// <summary>A bootstrap resolver plus the disposable resources created for it.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly IDisposable[]? _owned;

        internal Lease(IConnectionSecretResolver resolver, IDisposable[]? owned)
        {
            Resolver = resolver;
            _owned = owned;
        }

        /// <summary>The resolver to use for this bootstrap step.</summary>
        public IConnectionSecretResolver Resolver { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_owned is null)
            {
                return;
            }

            foreach (var disposable in _owned)
            {
                disposable.Dispose();
            }
        }
    }

    private sealed class OverrideScope(IConnectionSecretResolver? previous) : IDisposable
    {
        public void Dispose() => _testOverride.Value = previous;
    }
}
