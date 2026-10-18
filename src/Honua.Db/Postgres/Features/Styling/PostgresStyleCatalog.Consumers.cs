// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Styling.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Styling;

internal sealed partial class PostgresStyleCatalog
{
    private async Task<StyleEditStoreSnapshot> ReadConsumersAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, StyleEditStoreSnapshot snapshot, IReadOnlyList<string> referenceIds,
        CancellationToken cancellationToken)
    {
        const int maximumConsumers = 1000;
        // An ordinary catalog style shadows a same-named layer alias, exactly as
        // the canonical stylesheet projection does. The canonical ID always remains.
        var syntheticAliases = referenceIds.Where(static reference =>
        {
            const string prefix = "style-layer-";
            return reference.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(reference.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && reference == prefix + id.ToString(CultureInfo.InvariantCulture);
        }).ToArray();
        string[] aliases;
        await using (var command = new NpgsqlCommand($"""
            SELECT DISTINCT reference FROM unnest(@references) AS reference
            WHERE reference = @canonical OR reference = ANY(@synthetic) OR NOT EXISTS (
                SELECT 1 FROM {_stylesTable} WHERE style_id = reference)
            ORDER BY reference
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("references", NpgsqlDbType.Array | NpgsqlDbType.Text, referenceIds.ToArray());
            command.Parameters.AddWithValue("canonical", snapshot.StyleId);
            command.Parameters.AddWithValue("synthetic", NpgsqlDbType.Array | NpgsqlDbType.Text, syntheticAliases);
            List<string> found = [];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) found.Add(reader.GetString(0));
            aliases = [.. found];
        }
        var common = $"""
            owners AS (
                SELECT 'version'::text AS owner_kind, version.version_id AS owner_id,
                    version.item_id, version.envelope ->> 'family' AS family, version.package_key, version.version_id,
                    NULL::uuid AS draft_id, NULL::bigint AS generation, version.content_hash,
                    version.owner_id AS actor_id, version.tenant_id, version.workspace_id,
                    COALESCE(item.current_version_id = version.version_id, FALSE) AS is_current,
                    COALESCE(item.published_version_id = version.version_id, FALSE) AS is_published,
                    item.package_key AS identity_key, item.workspace_id AS identity_workspace,
                    item.tenant_id AS identity_tenant, item.family AS identity_family
                FROM {_studioVersionsTable} AS version JOIN {_studioItemsTable} AS item USING(item_id)
                UNION ALL
                SELECT 'draft', draft.draft_id, draft.item_id, draft.family, draft.package_key,
                    NULL::uuid, draft.draft_id, draft.generation,
                    encode(sha256(convert_to(draft.envelope::text, 'UTF8')), 'hex'),
                    draft.owner_id, draft.tenant_id, draft.workspace_id, FALSE, FALSE,
                    item.package_key, item.workspace_id, item.tenant_id, item.family
                FROM {_studioDraftsTable} AS draft JOIN {_studioItemsTable} AS item USING(item_id)
            ), dependencies AS (
                SELECT 'version'::text AS owner_kind, version_id AS owner_id, dependency_kind, dependency_ref
                    FROM {_studioDependenciesTable} WHERE dependency_kind IN ('server-style-coverage', 'server-live-style', 'server-map-key', 'server-item-reference', 'server-version-reference')
                UNION ALL
                SELECT 'draft', draft_id, dependency_kind, dependency_ref FROM {_studioDraftDependenciesTable}
            )
            """;
        var sql = $"""
            WITH RECURSIVE {common}, affected(owner_kind, owner_id) AS (
                SELECT owner_kind, owner_id FROM dependencies
                    WHERE dependency_kind = 'server-live-style' AND dependency_ref = ANY(@aliases)
                UNION
                SELECT parent.owner_kind, parent.owner_id FROM dependencies AS parent
                JOIN owners AS parent_owner ON parent.owner_kind = parent_owner.owner_kind AND parent.owner_id = parent_owner.owner_id
                JOIN owners AS child ON (
                    (parent.dependency_kind = 'server-item-reference' AND child.owner_kind = 'version' AND parent.dependency_ref = child.item_id::text)
                    OR (parent.dependency_kind = 'server-map-key' AND child.owner_kind = 'version' AND child.identity_family = 'map'
                        AND parent.dependency_ref = child.identity_key
                        AND COALESCE(NULLIF(BTRIM(child.identity_workspace), ''), '') = COALESCE(NULLIF(BTRIM(parent_owner.workspace_id), ''), ''))
                    OR (parent.dependency_kind = 'server-version-reference' AND child.owner_kind = 'version'
                        AND parent.dependency_ref = child.owner_id::text))
                    AND child.tenant_id IS NOT DISTINCT FROM parent_owner.tenant_id
                    AND child.identity_tenant IS NOT DISTINCT FROM parent_owner.tenant_id
                JOIN affected ON affected.owner_kind = child.owner_kind AND affected.owner_id = child.owner_id
            )
            SELECT owner.family, owner.item_id, owner.package_key, owner.version_id, owner.draft_id,
                owner.generation, owner.content_hash, owner.actor_id, owner.tenant_id,
                owner.is_current, owner.is_published, owner.workspace_id
            FROM owners AS owner JOIN affected USING(owner_kind, owner_id)
            ORDER BY owner.owner_kind, owner.owner_id LIMIT 1001;
            WITH {common}
            SELECT NOT EXISTS (
                SELECT 1 FROM owners AS owner
                WHERE owner.family IN ('map', 'app', 'dashboard', 'report') AND NOT EXISTS (
                    SELECT 1 FROM dependencies AS dependency
                    WHERE dependency.owner_kind = owner.owner_kind AND dependency.owner_id = owner.owner_id
                        AND dependency.dependency_kind = 'server-style-coverage' AND dependency.dependency_ref = 'complete'))
            AND NOT EXISTS (
                SELECT 1 FROM dependencies AS reference
                JOIN owners AS parent ON reference.owner_kind = parent.owner_kind AND reference.owner_id = parent.owner_id
                WHERE reference.dependency_kind IN ('server-map-key', 'server-item-reference', 'server-version-reference')
                    AND NOT EXISTS (
                        SELECT 1 FROM owners AS target WHERE target.owner_kind = 'version'
                            AND target.tenant_id IS NOT DISTINCT FROM parent.tenant_id
                            AND target.identity_tenant IS NOT DISTINCT FROM parent.tenant_id AND (
                            (reference.dependency_kind = 'server-map-key' AND target.identity_family = 'map'
                                AND COALESCE(NULLIF(BTRIM(target.identity_workspace), ''), '') = COALESCE(NULLIF(BTRIM(parent.workspace_id), ''), '') AND target.identity_key = reference.dependency_ref)
                            OR (reference.dependency_kind = 'server-item-reference' AND target.item_id::text = reference.dependency_ref)
                            OR (reference.dependency_kind = 'server-version-reference' AND target.owner_id::text = reference.dependency_ref))))
            """;
        await using var query = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
        query.Parameters.AddWithValue("aliases", NpgsqlDbType.Array | NpgsqlDbType.Text, aliases);
        List<StylePackageConsumer> consumers = [];
        await using var rows = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false)) consumers.Add(new(
            rows.GetString(0), rows.GetGuid(1), rows.GetString(2), rows.IsDBNull(3) ? null : rows.GetGuid(3),
            rows.IsDBNull(4) ? null : rows.GetGuid(4), rows.IsDBNull(5) ? null : rows.GetInt64(5), rows.GetString(6),
            rows.IsDBNull(7) ? null : rows.GetString(7), rows.IsDBNull(8) ? null : rows.GetString(8),
            rows.GetBoolean(9), rows.GetBoolean(10), rows.IsDBNull(11) ? null : rows.GetString(11)));
        var complete = await rows.NextResultAsync(cancellationToken).ConfigureAwait(false)
            && await rows.ReadAsync(cancellationToken).ConfigureAwait(false) && rows.GetBoolean(0)
            && consumers.Count <= maximumConsumers;
        return snapshot with { Consumers = consumers.Take(maximumConsumers).ToArray(), ConsumerIndexComplete = complete, ReferenceIds = aliases };
    }
}
