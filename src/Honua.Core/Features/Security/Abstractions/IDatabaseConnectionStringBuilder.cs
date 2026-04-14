// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Service for building secure database connection strings.
/// </summary>
public interface IDatabaseConnectionStringBuilder
{
    /// <summary>
    /// Builds a connection string from individual components.
    /// </summary>
    /// <param name="host">Database host.</param>
    /// <param name="port">Database port.</param>
    /// <param name="databaseName">Database name.</param>
    /// <param name="username">Database user name.</param>
    /// <param name="password">Database password.</param>
    /// <param name="sslMode">SSL mode.</param>
    /// <returns>The constructed connection string.</returns>
    string BuildConnectionString(
        string host,
        int port,
        string databaseName,
        string username,
        string password,
        SslMode sslMode);

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
