// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.ObjectStorage;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Import;

// The production resolver over a fixed set of stored connections and no built-ins.
internal sealed class StaticConnectionResolver(params ObjectStorageConnection[] connections) : IObjectStorageConnectionResolver
{
    public static readonly ObjectStorageConnection Customer = new()
    {
        Id = Guid.Parse("8d0f5c55-2a3e-4a8b-9e57-0f5c6f3b9a11"),
        Name = "Customer",
        Bucket = "bucket",
        Prefix = "layers/",
        Region = "us-east-1",
        RoleArn = "arn:aws:iam::123456789012:role/honua-connection-customer",
        ExternalId = "external-id-from-setup",
    };

    private readonly ObjectStorageConnectionResolver _inner = new(
        new ObjectStorageBuiltInSources(
            new StaticMonitor<CloudStorageOptions>(new()),
            new StaticMonitor<ObjectStorageConnectionOptions>(new()),
            []),
        new Store(connections));

    public Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
        => _inner.ListAsync(cancellationToken);

    public Task<ObjectStorageConnection?> FindAsync(Guid connectionId, CancellationToken cancellationToken)
        => _inner.FindAsync(connectionId, cancellationToken);

    public Task<ObjectStorageResolution> ResolveObjectAsync(Guid connectionId, string? key, CancellationToken cancellationToken)
        => _inner.ResolveObjectAsync(connectionId, key, cancellationToken);

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class Store(ObjectStorageConnection[] connections) : IObjectStorageConnectionStore
    {
        public Task<bool> SaveSetupAsync(ObjectStorageConnectionSetup setup, int maxOpenForMinter, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ObjectStorageConnectionSetup?> FindOpenSetupAsync(string externalId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ObjectStorageCreateOutcome> CreateAsync(ObjectStorageConnection connection, string mintedBy, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ObjectStorageConnection?> GetAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(connections.FirstOrDefault(connection => connection.Id == id));

        public Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObjectStorageConnection>>(connections);

        public Task<bool> UpdateAsync(ObjectStorageConnection connection, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
