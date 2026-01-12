// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Postgres.Features.Security;

internal sealed class PostgresConnectionStringBuilder : IDatabaseConnectionStringBuilder
{
    public string BuildConnectionString(
        string host,
        int port,
        string databaseName,
        string username,
        string password,
        CoreSslMode sslMode)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host is required.", nameof(host));
        }

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("Database name is required.", nameof(databaseName));
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        ArgumentNullException.ThrowIfNull(password);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = databaseName,
            Username = username,
            Password = password,
            SslMode = MapSslMode(sslMode)
        };

        return builder.ConnectionString;
    }

    private static Npgsql.SslMode MapSslMode(CoreSslMode sslMode) =>
        sslMode switch
        {
            CoreSslMode.Disable => Npgsql.SslMode.Disable,
            CoreSslMode.Allow => Npgsql.SslMode.Allow,
            CoreSslMode.Prefer => Npgsql.SslMode.Prefer,
            CoreSslMode.Require => Npgsql.SslMode.Require,
            CoreSslMode.VerifyCA => Npgsql.SslMode.VerifyCA,
            CoreSslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
            _ => throw new ArgumentOutOfRangeException(nameof(sslMode), sslMode, "Unsupported SSL mode.")
        };
}
