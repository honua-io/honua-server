// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

internal static class PostgresReaderCompatibilityJournal
{
    internal const string Table = "public.schema_version_reader_compatibility";

    internal static async Task<bool> TableExistsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT to_regclass('{Table}') IS NOT NULL";
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    internal static async Task<IReadOnlySet<string>> QualifyAsync(
        DbConnection connection,
        IReadOnlyList<string> unknownMigrations,
        int readerSchema,
        CancellationToken cancellationToken)
    {
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        if (unknownMigrations.Count == 0)
        {
            return accepted;
        }

        var declarations = await ReadAsync(connection, unknownMigrations, cancellationToken).ConfigureAwait(false);
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in unknownMigrations)
        {
            if (!declarations.TryGetValue(name, out var declaration))
            {
                continue;
            }

            var verdict = MigrationReaderCompatibility.Verify(declaration, name, readerSchema);
            if (verdict.Accepted)
            {
                accepted.Add(name);
            }
            else
            {
                refusals[name] = verdict.RefusalReason!;
            }
        }

        var refused = unknownMigrations.Where(name => !accepted.Contains(name)).ToArray();
        if (refused.Length > 0)
        {
            throw new DatabaseSchemaCompatibilityException(refused, refusals);
        }

        return accepted;
    }

    private static async Task<Dictionary<string, MigrationReaderCompatibilityDeclaration>> ReadAsync(
        DbConnection connection,
        IReadOnlyList<string> scriptNames,
        CancellationToken cancellationToken)
    {
        var declarations = new Dictionary<string, MigrationReaderCompatibilityDeclaration>(StringComparer.Ordinal);
        if (!await TableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return declarations;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT script_name, declaration_format, classification, script_sha256, min_reader_schema, script_text
            FROM {Table}
            WHERE script_name = ANY(@names)
            """;
        var names = command.CreateParameter();
        names.ParameterName = "names";
        names.Value = scriptNames.ToArray();
        command.Parameters.Add(names);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var declaration = new MigrationReaderCompatibilityDeclaration
            {
                ScriptName = reader.GetString(0),
                DeclarationFormat = reader.GetInt32(1),
                Classification = reader.GetString(2),
                ScriptSha256 = reader.GetString(3),
                MinReaderSchema = reader.GetInt32(4),
                ScriptText = reader.GetString(5),
            };
            declarations[declaration.ScriptName] = declaration;
        }

        return declarations;
    }
}
