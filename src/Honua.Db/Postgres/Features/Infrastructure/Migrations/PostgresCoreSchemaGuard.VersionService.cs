// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

internal sealed partial class PostgresCoreSchemaGuard
{
    private async Task VerifyVersionServiceAssociationAsync(
        DbConnection connection, SchemaState state, bool allowPending, CancellationToken cancellationToken)
    {
        if (_migrations.VersionServiceAssociationMigration is not { } migration)
        {
            return;
        }
        await using var column = connection.CreateCommand();
        column.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'honua' AND c.relname = 'gdb_versions' AND c.relkind = 'r'
                  AND a.attname = 'service_id' AND a.attnum > 0 AND NOT a.attisdropped)
            """;
        var exists = (bool)(await column.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
        if (!state.IsApplied(migration))
        {
            if (allowPending && !exists)
            {
                return;
            }
            throw CreateFailure(migration,
                exists ? DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal : DatabaseSchemaFloorFailureKind.MigrationNotApplied,
                "The branch service-association migration is not recorded in the application journal.");
        }
        if (!exists)
        {
            throw CreateFailure(migration, DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                "The journal claims branch service association, but the canonical registry column is absent.");
        }
        await using var shape = connection.CreateCommand();
        shape.CommandText = """
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'honua' AND table_name = 'gdb_versions'
                  AND column_name = 'service_id' AND data_type = 'text' AND is_nullable = 'YES')
            """;
        if (!(bool)(await shape.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
        {
            throw CreateFailure(migration, DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
                "Branch service association requires a nullable text column so legacy identities remain unchanged.");
        }
    }
}
