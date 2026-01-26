// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Tests database connectivity for a supplied connection string.
/// </summary>
public interface IConnectionHealthTester
{
    /// <summary>
    /// Attempts to open a connection and run a lightweight query.
    /// </summary>
    /// <param name="connectionString">Connection string to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the connection is healthy.</returns>
    Task<bool> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default);
}
