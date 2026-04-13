// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Service for building secure database connection strings.
/// </summary>
public interface IDatabaseConnectionStringBuilder
{
    /// <summary>
    /// Builds a connection string from a data connection configuration.
    /// </summary>
    /// <param name="connection">The data connection configuration</param>
    /// <returns>The constructed connection string</returns>
    Task<string> BuildConnectionStringAsync(DataConnection connection);

    /// <summary>
    /// Validates a connection string format.
    /// </summary>
    /// <param name="connectionString">The connection string to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    bool ValidateConnectionString(string connectionString);
}