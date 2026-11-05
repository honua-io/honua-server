// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

namespace Honua.Server.Startup;

internal static class BootstrapSecretResolver
{
    private static readonly AsyncLocal<IConnectionSecretResolver?> _testOverride = new();

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

    internal static IDisposable UseForTesting(IConnectionSecretResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var previous = _testOverride.Value;
        _testOverride.Value = resolver;
        return new OverrideScope(previous);
    }

    internal sealed class Lease : IDisposable
    {
        private readonly IDisposable[]? _owned;

        internal Lease(IConnectionSecretResolver resolver, IDisposable[]? owned)
        {
            Resolver = resolver;
            _owned = owned;
        }

        public IConnectionSecretResolver Resolver { get; }

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
