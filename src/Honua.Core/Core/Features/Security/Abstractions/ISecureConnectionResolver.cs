// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

public interface ISecureConnectionResolver
{
    Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default);

    Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default);

    Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default);
}
