// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Builds database connection strings for secure connection workflows.
/// </summary>
public interface IDatabaseConnectionStringBuilder
{
    /// <summary>
    /// Builds a connection string from the supplied connection details.
    /// </summary>
    /// <param name="host">Database host name.</param>
    /// <param name="port">Database port.</param>
    /// <param name="databaseName">Database name.</param>
    /// <param name="username">Database username.</param>
    /// <param name="password">Database password.</param>
    /// <param name="sslMode">SSL/TLS mode.</param>
    /// <returns>Connection string suitable for the configured provider.</returns>
    string BuildConnectionString(
        string host,
        int port,
        string databaseName,
        string username,
        string password,
        SslMode sslMode);
}
