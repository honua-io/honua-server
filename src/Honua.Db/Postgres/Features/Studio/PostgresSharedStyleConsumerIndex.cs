// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Styling;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Studio;

internal sealed class PostgresSharedStyleConsumerIndex : ISharedStyleConsumerIndex
{
    private readonly IAdoNetDatabaseConnectionProvider _connections;
    private readonly string _items;
    private readonly string _versions;
    private readonly string _drafts;

    public PostgresSharedStyleConsumerIndex(IAdoNetDatabaseConnectionProvider connections, string? schemaName)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _items = SchemaSearchPath.QualifyTable("studio_content_items", schemaName);
        _versions = SchemaSearchPath.QualifyTable("studio_content_versions", schemaName);
        _drafts = SchemaSearchPath.QualifyTable("studio_package_drafts", schemaName);
    }

    public async Task<SharedStyleConsumerScan> ListConsumersAsync(
        SharedStyleConsumerQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!SharedStyleReferences.CanMatchLiterally(query.StyleId) || query.Limit < 1)
        {
            return new SharedStyleConsumerScan { Consumers = [], Complete = false };
        }

        var scan = new Scan(query.StyleId, query.Limit, query.Tenant);
        try
        {
            await using var lease = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            scan.Connection = lease.Connection;
            await ReadVersionsAsync(scan, cancellationToken).ConfigureAwait(false);
            await ReadDraftsAsync(scan, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Hosts that have not applied the studio package migrations have no map or app
            // consumers. The scan is complete: there is nothing further to read.
            return new SharedStyleConsumerScan { Consumers = [], Complete = true };
        }

        return scan.ToResult();
    }

    private async Task ReadVersionsAsync(Scan scan, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT i.item_id::text, i.package_key, i.family, v.version_id::text, v.content_hash,
                   v.version_number,
                   (i.published_version_id IS NOT NULL AND i.published_version_id = v.version_id) AS published,
                   (i.current_version_id IS NOT NULL AND i.current_version_id = v.version_id) AS is_current
            FROM {_items} AS i
            JOIN {_versions} AS v
              ON v.version_id = i.current_version_id OR v.version_id = i.published_version_id
            WHERE i.family IN ('map', 'app') AND {TenantPredicate(scan.Tenant, "i.tenant_id")}
              AND (strpos(v.envelope::text, @style_id) > 0 OR strpos(v.envelope::text, @style_id_spaced) > 0
                   OR strpos(v.envelope::text, @style_ref) > 0 OR strpos(v.envelope::text, @style_ref_spaced) > 0)
            ORDER BY i.package_key, i.item_id, v.version_id
            LIMIT @take
            """;
        await using var command = new NpgsqlCommand(sql, scan.Connection);
        AddNeedles(command, scan.StyleId);
        AddTenant(command, scan.Tenant);
        command.Parameters.AddWithValue("@take", scan.Take);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scan.Add(new SharedStyleConsumerRow
            {
                Kind = reader.GetString(2),
                ItemId = reader.GetString(0),
                PackageKey = reader.GetString(1),
                ContentHash = reader.GetString(4),
                PublicationState = reader.GetBoolean(6) ? "published" : "saved",
                VersionId = reader.GetString(3),
                Generation = reader.GetInt32(5),
                IsCurrent = reader.GetBoolean(7)
            });
        }
    }

    private async Task ReadDraftsAsync(Scan scan, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT item_id::text, package_key, family, draft_id::text, generation, envelope::text
            FROM {_drafts}
            WHERE family IN ('map', 'app') AND {TenantPredicate(scan.Tenant, "tenant_id")}
              AND (strpos(envelope::text, @style_id) > 0 OR strpos(envelope::text, @style_id_spaced) > 0
                   OR strpos(envelope::text, @style_ref) > 0 OR strpos(envelope::text, @style_ref_spaced) > 0)
            ORDER BY package_key, item_id, draft_id
            LIMIT @take
            """;
        await using var command = new NpgsqlCommand(sql, scan.Connection);
        AddNeedles(command, scan.StyleId);
        AddTenant(command, scan.Tenant);
        command.Parameters.AddWithValue("@take", scan.Take);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var envelope = reader.GetString(5);
            scan.Add(new SharedStyleConsumerRow
            {
                Kind = reader.GetString(2),
                ItemId = reader.GetString(0),
                PackageKey = reader.GetString(1),
                ContentHash = SharedStyleIdentity.ContentHash(envelope),
                PublicationState = "draft",
                DraftId = reader.GetString(3),
                Generation = reader.GetInt64(4),
                IsCurrent = true
            });
        }
    }

    private sealed class Scan
    {
        private readonly List<SharedStyleConsumerRow> _consumers = [];

        public Scan(string styleId, int limit, TenantScopeFilter? tenant)
        {
            StyleId = styleId;
            Limit = limit;
            Tenant = tenant;
        }

        public string StyleId { get; }

        public TenantScopeFilter? Tenant { get; }

        public int Limit { get; }

        public int Take => Limit + 1;

        public NpgsqlConnection Connection { get; set; } = null!;

        public void Add(SharedStyleConsumerRow row) => _consumers.Add(row);

        public SharedStyleConsumerScan ToResult()
        {
            var complete = _consumers.Count <= Limit;
            if (!complete)
            {
                _consumers.RemoveRange(Limit, _consumers.Count - Limit);
            }

            return new SharedStyleConsumerScan { Consumers = _consumers, Complete = complete };
        }
    }

    // Same semantics as the Studio content lists: no filter for an unscoped caller, unassigned
    // rows only for the default tenant, and nothing for a tenant-less scoped caller.
    private static string TenantPredicate(TenantScopeFilter? tenant, string column) => tenant switch
    {
        null => "TRUE",
        { TenantId: null } => tenant.IncludeUnassigned ? $"{column} IS NULL" : "FALSE",
        _ => tenant.IncludeUnassigned ? $"({column} = @tenant_id OR {column} IS NULL)" : $"{column} = @tenant_id",
    };

    private static void AddTenant(NpgsqlCommand command, TenantScopeFilter? tenant)
    {
        if (tenant?.TenantId is { } tenantId)
        {
            command.Parameters.AddWithValue("@tenant_id", tenantId);
        }
    }

    private static void AddNeedles(NpgsqlCommand command, string styleId)
    {
        command.Parameters.AddWithValue("@style_id", SharedStyleReferences.StyleIdNeedle(styleId));
        command.Parameters.AddWithValue("@style_id_spaced", SharedStyleReferences.StyleIdNeedleSpaced(styleId));
        command.Parameters.AddWithValue("@style_ref", SharedStyleReferences.StyleRefNeedle(styleId));
        command.Parameters.AddWithValue("@style_ref_spaced", SharedStyleReferences.StyleRefNeedleSpaced(styleId));
    }
}
