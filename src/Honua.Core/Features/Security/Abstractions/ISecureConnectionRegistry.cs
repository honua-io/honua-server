// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

public interface ISecureConnectionRegistry
{
    Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default);

    Task RegisterConnectionAsync(DataConnection connection);

    Task<DataConnection?> GetConnectionAsync(string connectionId);

    Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    Task<IEnumerable<DataConnection>> GetAllConnectionsAsync();

    Task<bool> RemoveConnectionAsync(string connectionId);

    Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync();

    Task<DataConnection?> GetConnectionByNameAsync(string connectionName, CancellationToken cancellationToken = default);

    Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken);

    Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default);

    Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default);

    Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default);
}
