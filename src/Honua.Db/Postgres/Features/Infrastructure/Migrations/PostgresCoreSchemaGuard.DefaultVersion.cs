// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

internal sealed partial class PostgresCoreSchemaGuard
{
    private async Task VerifyDefaultVersionIdentityAsync(
        DbConnection connection, SchemaState state, bool allowPending, CancellationToken cancellationToken)
    {
        if (_migrations.DefaultVersionIdentityMigration is not { } migration)
        {
            return;
        }

        // Like the canonical gdb_versions registry, this identity belongs to honua, not a
        // publication's configurable feature schema. A same-name external table cannot satisfy it.
        await using var table = connection.CreateCommand();
        table.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'honua' AND c.relname = 'gdb_version_store_identity' AND c.relkind = 'r')
            """;
        var exists = (bool)(await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
        if (!state.IsApplied(migration))
        {
            if (allowPending && !exists)
            {
                return;
            }

            throw CreateFailure(migration,
                exists ? DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal : DatabaseSchemaFloorFailureKind.MigrationNotApplied,
                "The durable DEFAULT identity migration is not recorded in the application journal.");
        }

        if (!exists)
        {
            throw CreateFailure(migration, DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                "The journal claims the durable DEFAULT identity migration, but its canonical table is absent.");
        }

        await using var shape = connection.CreateCommand();
        shape.CommandText = """
            SELECT count(*) = 3
            FROM information_schema.columns
            WHERE table_schema = 'honua' AND table_name = 'gdb_version_store_identity'
              AND ((column_name = 'singleton' AND data_type = 'boolean')
                OR (column_name = 'version_id' AND data_type = 'uuid')
                OR (column_name = 'created_at' AND data_type = 'timestamp with time zone'))
            """;
        if (!(bool)(await shape.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
        {
            throw CreateFailure(migration, DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                "The durable DEFAULT identity table is missing its required typed columns.");
        }

        await using var row = connection.CreateCommand();
        row.CommandText = """
            SELECT count(*) = 1 AND COALESCE(bool_and(singleton IS TRUE
                AND version_id IS NOT NULL
                AND version_id <> '00000000-0000-0000-0000-000000000000'::uuid
                AND created_at IS NOT NULL), false)
            FROM honua.gdb_version_store_identity
            """;
        if (!(bool)(await row.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
        {
            throw CreateFailure(migration, DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                "The durable DEFAULT identity must contain exactly one valid non-nil persisted identity.");
        }
    }
}
