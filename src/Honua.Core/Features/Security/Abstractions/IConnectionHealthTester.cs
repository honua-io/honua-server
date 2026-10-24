// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Service for testing connection health.
/// </summary>
public interface IConnectionHealthTester
{
    /// <summary>
    /// Tests the health of a connection.
    /// </summary>
    /// <param name="connectionString">Connection string to test</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The health status of the connection</returns>
    Task<ConnectionHealthStatus> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default);
}
