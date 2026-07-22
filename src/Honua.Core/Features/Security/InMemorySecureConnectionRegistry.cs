// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security;

/// <summary>
/// Process-local, non-durable <see cref="ISecureConnectionRegistry"/> backed by an
/// in-memory dictionary. Registered by read-only primary feature providers (DuckDB,
/// MySQL/MariaDB) that have no <c>honua.data_connections</c>-equivalent catalog of their
/// own, so DI activation succeeds for <c>FeatureProviderQueryRouter</c> — a shared,
/// provider-independent service every read request resolves regardless of the active
/// data-source provider (see <c>InfrastructureCompositionRoot.RegisterInfrastructureServices</c>).
/// </summary>
/// <remarks>
/// <para>
/// Found and fixed under honua-server#2947 (secondary-provider HTTP-stack GA proof):
/// with no <see cref="ISecureConnectionRegistry"/> registration at all, EVERY FeatureServer
/// and OGC API Features request failed DI activation outright under
/// <c>DataSource:Provider=duckdb</c> or <c>mysql</c>. Only <c>Honua.Postgres</c> ever
/// registered a durable, database-backed implementation
/// (<c>PostgresSecureConnectionRegistry</c>, storing rows in <c>honua.data_connections</c>).
/// </para>
/// <para>
/// This is an honest, functional (not a no-op) implementation: connections registered
/// through it are genuinely stored and retrievable for the lifetime of the process, which
/// is sufficient for the router's lookup contract and for a standalone DuckDB/MySQL
/// deployment that layers a secondary/additional provider (e.g. SQL Server) on top. What it
/// does <b>not</b> provide is durability across restarts or multi-instance sharing — DuckDB
/// and MySQL are documented as requiring no external catalog infrastructure of their own, so
/// there is no natural place to persist connections durably without introducing one. A
/// deployment that needs durable secure connections alongside DuckDB/MySQL as primary should
/// register the Postgres security services explicitly, if available.
/// </para>
/// </remarks>
public sealed class InMemorySecureConnectionRegistry : ISecureConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, DataConnection> _byId = new();

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task RegisterConnectionAsync(DataConnection connection) => CreateConnectionAsync(connection);

    /// <inheritdoc />
    public Task<DataConnection?> GetConnectionAsync(string connectionId)
        => Task.FromResult(Guid.TryParse(connectionId, out var id) && _byId.TryGetValue(id, out var connection)
            ? connection
            : null);

    /// <inheritdoc />
    public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.TryGetValue(connectionId, out var connection) ? connection : null);

    /// <inheritdoc />
    /// <remarks>
    /// Metadata V2 connection ids are not required to be GUIDs (test graphs and metadata
    /// commonly use names like <c>conn-1</c>), so — matching
    /// <c>PostgresSecureConnectionRegistry.GetConnectionAsync(string, CancellationToken)</c> —
    /// a non-GUID id falls back to a name lookup instead of returning null. Without this,
    /// <c>FeatureProviderQueryRouter</c> cannot resolve an in-memory-registered connection by
    /// its metadata id/name, so secondary-provider publications either fail as missing or
    /// route without their <see cref="DataConnection"/>.
    /// </remarks>
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

    /// <inheritdoc />
    public Task<DataConnection?> GetConnectionByNameAsync(string connectionName, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.Values.FirstOrDefault(c => string.Equals(c.Name, connectionName, StringComparison.Ordinal)));

    /// <inheritdoc />
    public Task<IEnumerable<DataConnection>> GetAllConnectionsAsync()
        => Task.FromResult<IEnumerable<DataConnection>>(_byId.Values.ToArray());

    /// <inheritdoc />
    public Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<DataConnection>>(_byId.Values.Where(c => c.IsActive).ToArray());

    /// <inheritdoc />
    public Task<bool> RemoveConnectionAsync(string connectionId)
        => Task.FromResult(Guid.TryParse(connectionId, out var id) && _byId.TryRemove(id, out _));

    /// <inheritdoc />
    public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.TryRemove(connectionId, out _));

    /// <inheritdoc />
    public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _byId[connection.ConnectionId] = connection;
        return Task.FromResult(connection);
    }

    /// <inheritdoc />
    public Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default)
    {
        if (Guid.TryParse(connectionId, out var id) && _byId.TryGetValue(id, out var connection))
        {
            connection.HealthStatus = isHealthy ? ConnectionHealthStatus.Healthy : ConnectionHealthStatus.Unhealthy;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync()
        => Task.FromResult(_byId.Values.ToDictionary(c => c.ConnectionId.ToString(), c => c.HealthStatus));
}
