// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Admin.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private sealed record PublicationFingerprintContext(NpgsqlConnection Connection, NpgsqlTransaction Transaction,
        LayerPublishRequest Request, PreparedPublicationSource Source);

    private async Task VerifyPublicationSourceFingerprintAsync(PublicationFingerprintContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (request.ExpectedSourceFingerprint is not { } expected)
        {
            return;
        }
        var current = await ReadPublicationSourceFingerprintAsync(context, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(expected, current, StringComparison.Ordinal))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "The publication preview source changed. Create a new preview.");
        }
    }

    // Call only while holding the import session lock and the relation's ACCESS SHARE lock.
    // Relation OID detects replacement; ordered physical attributes and constraints bind implicit fields too.
    private async Task<string> ReadPublicationSourceFingerprintAsync(PublicationFingerprintContext context,
        CancellationToken cancellationToken)
    {
        var (connection, transaction, request, source) = context;
        var schema = request.Schema.Trim();
        var table = request.Table.Trim();
        var epoch = await ReadImportedStorageEpochAsync(new StorageEpochContext(connection, transaction, request, schema, table,
            source.GeometryColumn, source.StorageSrid, source.StorageSrid), cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('database', current_database(), 'relation', c.oid, 'kind', c.relkind,
                'columns', (SELECT jsonb_agg(jsonb_build_array(a.attnum, a.attname, a.atttypid,
                    a.atttypmod, a.attnotnull, a.attcollation, a.attidentity, a.attgenerated) ORDER BY a.attnum)
                    FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped),
                'constraints', (SELECT jsonb_agg(pg_get_constraintdef(k.oid) ORDER BY k.conname)
                    FROM pg_constraint k WHERE k.conrelid = c.oid))::text
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        var shape = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "Publication source is unavailable.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            shape + "\n" + epoch?.ToString("R", CultureInfo.InvariantCulture))));
    }
}
