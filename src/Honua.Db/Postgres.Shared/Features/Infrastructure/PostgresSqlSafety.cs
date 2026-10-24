// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Sql;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

internal static class PostgresSqlSafety
{
    public static NpgsqlCommand CreateReadCommand(NpgsqlConnection connection, string sql)
    {
        ValidateReadOnlySingleStatement(sql);
        return new NpgsqlCommand(sql, connection);
    }

    public static NpgsqlCommand CreateAnalyzeCommand(NpgsqlConnection connection, string tableName)
    {
        var quotedTableName = SchemaSearchPath.ValidateAndQuote(tableName);
        return new NpgsqlCommand($"ANALYZE {quotedTableName}", connection);
    }

    public static NpgsqlCommand CreateAnalyzeCommand(NpgsqlConnection connection, string schemaName, string tableName)
    {
        var quotedSchemaName = SchemaSearchPath.ValidateAndQuote(schemaName);
        var quotedTableName = SchemaSearchPath.ValidateAndQuote(tableName);
        return new NpgsqlCommand($"ANALYZE {quotedSchemaName}.{quotedTableName}", connection);
    }

    public static bool IsReadOnlySingleStatement(string sql)
    {
        try
        {
            ValidateReadOnlySingleStatement(sql);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void ValidateReadOnlySingleStatement(string sql)
        => ReadOnlyDynamicSql.ValidateReadOnlySingleStatement(sql);
}
