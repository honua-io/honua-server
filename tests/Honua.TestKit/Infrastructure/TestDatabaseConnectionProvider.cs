// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of database connection provider for unit tests
/// </summary>
internal sealed class TestDatabaseConnectionProvider : IDatabaseConnectionProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Func<string?> _schemaProvider;

    public TestDatabaseConnectionProvider(NpgsqlDataSource dataSource, Func<string?>? schemaProvider = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schemaProvider = schemaProvider ?? (() => null);
    }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var schemaName = _schemaProvider();
        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            ValidateSchemaName(schemaName);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET search_path TO {schemaName}, public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }

    private static void ValidateSchemaName(string schemaName)
    {
        foreach (var ch in schemaName)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                throw new ArgumentException($"Invalid schema name '{schemaName}'.", nameof(schemaName));
            }
        }
    }
}
