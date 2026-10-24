// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

/// <summary>
/// Resolves the existing attachment authority without moving or creating objects.
/// Migration 002 historically created the table in honua; migration 125 added
/// its import columns and cleanup table. Missing cleanup is legitimate only
/// before 125, never a reason to create a second authority on a request path.
/// </summary>
internal static class PostgresAttachmentSchema
{
    private static readonly string[] _cleanupColumns = ["storage_path", "layer_id", "feature_id"];

    internal static async Task<string> ResolveAsync(
        NpgsqlConnection connection,
        string? configuredSchema,
        PostgresAttachmentMigrationContract contract,
        NpgsqlTransaction? transaction = null,
        bool allowPendingCreation = false,
        bool requireImportCleanup = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var configured = string.IsNullOrWhiteSpace(configuredSchema) ? "honua" : configuredSchema;
        _ = SchemaSearchPath.ValidateAndQuote(configured);
        var objects = new Dictionary<(string Schema, string Table), char>();
        await using (var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN (@configured, 'honua')
              AND c.relname IN ('attachments', 'import_attachment_cleanup')
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("configured", configured);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objects.Add((reader.GetString(0), reader.GetString(1)), reader.GetString(2)[0]);
            }
        }

        if (objects.Values.Any(kind => kind is not ('r' or 'p')))
        {
            throw new AttachmentSchemaBindingException("Attachment family contains a non-table object.");
        }

        var created = false;
        var imported = false;
        await using (var journal = new NpgsqlCommand(
            "SELECT to_regclass('public.schema_versions') IS NOT NULL", connection, transaction))
        {
            if (await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                journal.CommandText = """
                    SELECT EXISTS (SELECT 1 FROM public.schema_versions WHERE scriptname = @created),
                           EXISTS (SELECT 1 FROM public.schema_versions WHERE scriptname = @imported)
                    """;
                journal.Parameters.AddWithValue("created", contract.CreateMigration);
                journal.Parameters.AddWithValue("imported", contract.ImportMigration);
                await using var reader = await journal.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                created = reader.GetBoolean(0);
                imported = reader.GetBoolean(1);
            }
        }

        if (objects.Count == 0)
        {
            if (allowPendingCreation && !created && !imported)
            {
                return configured;
            }

            throw new AttachmentSchemaBindingException("Attachment family is absent despite its required migration phase.");
        }

        var attachments = objects.Keys.Where(key => key.Table == "attachments").ToArray();
        if (attachments.Length != 1)
        {
            throw new AttachmentSchemaBindingException("Attachment family is partial or has duplicate authorities.");
        }

        var physical = attachments[0].Schema;
        if (objects.Keys.Any(key => key.Schema != physical) ||
            ((imported || requireImportCleanup) && !objects.ContainsKey((physical, "import_attachment_cleanup"))))
        {
            throw new AttachmentSchemaBindingException("Attachment and import cleanup authority is partial or split between schemas.");
        }

        var columns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var types = new Dictionary<(string Table, string Column), string>();
        await using (var shape = new NpgsqlCommand("""
            SELECT c.relname, a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod)
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @physical AND a.attnum > 0 AND NOT a.attisdropped
              AND c.relname IN ('attachments', 'import_attachment_cleanup')
            """, connection, transaction))
        {
            shape.Parameters.AddWithValue("physical", physical);
            await using var reader = await shape.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                if (!columns.TryGetValue(table, out var names))
                {
                    names = new HashSet<string>(StringComparer.Ordinal);
                    columns.Add(table, names);
                }

                names.Add(reader.GetString(1));
                types.Add((table, reader.GetString(1)), reader.GetString(2));
            }
        }

        string[] baseColumns = ["id", "feature_id", "layer_id", "filename", "content_type", "size", "created_at", "storage_path", "keywords"];
        string[] importColumns = ["attachment_origin", "import_source", "import_parent_id", "import_attachment_id", "import_generation"];
        if (!columns.TryGetValue("attachments", out var attachmentColumns) ||
            !baseColumns.All(attachmentColumns.Contains))
        {
            throw new AttachmentSchemaBindingException("Attachment authority has an incomplete base shape.");
        }

        var importColumnCount = importColumns.Count(attachmentColumns.Contains);
        if ((importColumnCount != 0 && importColumnCount != importColumns.Length) ||
            ((imported || requireImportCleanup) && importColumnCount != importColumns.Length) ||
            (objects.ContainsKey((physical, "import_attachment_cleanup")) &&
             (!columns.TryGetValue("import_attachment_cleanup", out var cleanupColumns) ||
              !_cleanupColumns.All(cleanupColumns.Contains))))
        {
            throw new AttachmentSchemaBindingException("Attachment import authority has an incomplete migration shape.");
        }

        var expectedTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "bigint",
            ["feature_id"] = "bigint",
            ["layer_id"] = "integer",
            ["filename"] = "text",
            ["content_type"] = "text",
            ["size"] = "bigint",
            ["created_at"] = "timestamp with time zone",
            ["storage_path"] = "text",
            ["keywords"] = "text",
            ["attachment_origin"] = "text",
            ["import_source"] = "text",
            ["import_parent_id"] = "bigint",
            ["import_attachment_id"] = "bigint",
            ["import_generation"] = "uuid"
        };
        if (attachmentColumns.Any(column => expectedTypes.TryGetValue(column, out var expected) &&
                types[("attachments", column)] != expected) ||
            (columns.ContainsKey("import_attachment_cleanup") &&
             (types[("import_attachment_cleanup", "storage_path")] != "text" ||
              types[("import_attachment_cleanup", "layer_id")] != "integer" ||
              types[("import_attachment_cleanup", "feature_id")] != "bigint")))
        {
            throw new AttachmentSchemaBindingException("Attachment family column types do not match the canonical store contract.");
        }

        var attachmentPrimaryKey = false;
        var cleanupPrimaryKey = false;
        var importUniqueKey = false;
        await using (var keys = new NpgsqlCommand("""
            SELECT c.relname, con.contype::text,
                ARRAY(SELECT a.attname::text FROM unnest(con.conkey) WITH ORDINALITY k(attnum, position)
                      JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
                      ORDER BY k.position)
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @physical AND con.contype IN ('p', 'u') AND con.convalidated
              AND c.relname IN ('attachments', 'import_attachment_cleanup')
            """, connection, transaction))
        {
            keys.Parameters.AddWithValue("physical", physical);
            await using var reader = await keys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                var kind = reader.GetString(1);
                var names = reader.GetFieldValue<string[]>(2);
                attachmentPrimaryKey |= table == "attachments" && kind == "p" && names.SequenceEqual(["id"]);
                cleanupPrimaryKey |= table == "import_attachment_cleanup" && kind == "p" && names.SequenceEqual(["storage_path"]);
                importUniqueKey |= table == "attachments" && kind == "u" &&
                    names.SequenceEqual(["layer_id", "import_source", "import_parent_id", "import_attachment_id"]);
            }
        }

        if (!attachmentPrimaryKey || (columns.ContainsKey("import_attachment_cleanup") && !cleanupPrimaryKey) ||
            ((imported || requireImportCleanup) && !importUniqueKey))
        {
            throw new AttachmentSchemaBindingException("Attachment family keys do not match the canonical store contract.");
        }

        return physical;
    }
}

internal sealed class AttachmentSchemaBindingException(string message) : InvalidOperationException(message);
