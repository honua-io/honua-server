// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security;

public sealed class InMemorySecureConnectionRegistry : ISecureConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, DataConnection> _byId = new();

    public Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.ConnectionId == Guid.Empty)
        {
            connection.ConnectionId = Guid.NewGuid();
        }

        if (!_byId.TryAdd(connection.ConnectionId, connection))
        {
            throw new InvalidOperationException($"A connection with id '{connection.ConnectionId}' already exists.");
        }

        return Task.FromResult(connection);
    }

    public Task RegisterConnectionAsync(DataConnection connection) => CreateConnectionAsync(connection);

    public Task<DataConnection?> GetConnectionAsync(string connectionId)
        => Task.FromResult(Guid.TryParse(connectionId, out var id) && _byId.TryGetValue(id, out var connection)
            ? connection
            : null);

    public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.TryGetValue(connectionId, out var connection) ? connection : null);

    // A non-GUID connection id falls back to a name lookup so metadata ids such as conn-1 still resolve.
    public async Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return null;
        }

        if (Guid.TryParse(connectionId, out var id))
        {
            return await GetConnectionAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return await GetConnectionByNameAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    public Task<DataConnection?> GetConnectionByNameAsync(string connectionName, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.Values.FirstOrDefault(c => string.Equals(c.Name, connectionName, StringComparison.Ordinal)));

    public Task<IEnumerable<DataConnection>> GetAllConnectionsAsync()
        => Task.FromResult<IEnumerable<DataConnection>>(_byId.Values.ToArray());

    public Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<DataConnection>>(_byId.Values.Where(c => c.IsActive).ToArray());

    public Task<bool> RemoveConnectionAsync(string connectionId)
        => Task.FromResult(Guid.TryParse(connectionId, out var id) && _byId.TryRemove(id, out _));

    public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.TryRemove(connectionId, out _));

    public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _byId[connection.ConnectionId] = connection;
        return Task.FromResult(connection);
    }

    public Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default)
    {
        if (Guid.TryParse(connectionId, out var id) && _byId.TryGetValue(id, out var connection))
        {
            connection.HealthStatus = isHealthy ? ConnectionHealthStatus.Healthy : ConnectionHealthStatus.Unhealthy;
        }

        return Task.CompletedTask;
    }

    public Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync()
        => Task.FromResult(_byId.Values.ToDictionary(c => c.ConnectionId.ToString(), c => c.HealthStatus));
}
