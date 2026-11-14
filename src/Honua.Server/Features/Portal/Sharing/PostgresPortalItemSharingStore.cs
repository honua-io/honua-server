// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Npgsql;

namespace Honua.Server.Features.Portal.Sharing;

internal sealed class PostgresPortalItemSharingStore(
    NpgsqlDataSource dataSource,
    IHttpContextAccessor contextAccessor,
    string? schema = null) : IPortalItemSharingStore
{
    private readonly string _table = PortalSharingTables.Qualify("portal_item_shares", schema);

    public async Task<PortalItemShareState> GetAsync(string itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return PortalItemShareState.None;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadAsync(connection, null, ReadScope().StorageKeys, itemId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        return row is null ? PortalItemShareState.None : new PortalItemShareState(false, false, row.Value.GroupIds);
    }

    public async Task<string?> GetOwnerAsync(string itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadAsync(connection, null, ReadScope().StorageKeys, itemId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        return row?.Owner;
    }

    public Task<PortalItemShareState> ShareAsync(
        string itemId, string owner, PortalItemShareRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return MutateAsync(itemId, owner, request, add: true, cancellationToken);
    }

    public Task<PortalItemShareState> UnshareAsync(
        string itemId, PortalItemShareRequest request, CancellationToken cancellationToken)
        => MutateAsync(itemId, null, request, add: false, cancellationToken);

    // Synchronous authorization uses PortalAudienceSnapshot hydrated before the handler.
    public PortalItemShareState Get(string itemId)
        => throw new InvalidOperationException("Durable Portal audiences require the request audience snapshot.");

    public bool IsVisibleTo(PortalItemShareState state, bool isAuthenticated, IReadOnlyCollection<string> memberGroupIds)
        => state.Everyone || (state.Org && isAuthenticated)
            || state.GroupIds.Any(group => memberGroupIds.Contains(group, StringComparer.OrdinalIgnoreCase));

    private async Task<PortalItemShareState> MutateAsync(
        string itemId, string? owner, PortalItemShareRequest request, bool add, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var scope = ReadScope();
        var tenant = scope.TenantId ?? string.Empty;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Serialize all keys for this item before inserting or locking rows. This
        // also covers a missing default row during a concurrent share/unshare.
        await using (var itemLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@item, 0))", connection, transaction))
        {
            itemLock.Parameters.AddWithValue("item", itemId);
            await itemLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (add)
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {_table} (tenant_key, item_id, owner_name, group_ids)
                VALUES (@tenant, @item, @owner, ARRAY[]::text[]) ON CONFLICT DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("item", itemId);
            insert.Parameters.AddWithValue("owner", owner!);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadAsync(connection, transaction, scope.StorageKeys, itemId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return PortalItemShareState.None;
        }

        var groups = current.Value.GroupIds.ToList();
        var requested = Normalize(request.GroupIds);
        if (add)
        {
            foreach (var group in requested.Where(group => !groups.Contains(group, StringComparer.OrdinalIgnoreCase)))
            {
                groups.Add(group);
            }
        }
        else
        {
            groups.RemoveAll(group => requested.Contains(group, StringComparer.OrdinalIgnoreCase));
        }

        await using var update = new NpgsqlCommand($"""
            UPDATE {_table} SET group_ids = @groups WHERE tenant_key = ANY(@tenants) AND item_id = @item
            """, connection, transaction);
        update.Parameters.AddWithValue("groups", groups.ToArray());
        update.Parameters.AddWithValue("tenants", scope.StorageKeys);
        update.Parameters.AddWithValue("item", itemId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new PortalItemShareState(false, false, groups);
    }

    private async Task<(string Owner, string[] GroupIds)?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string[] tenants,
        string itemId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT owner_name, group_ids FROM {_table}
            WHERE tenant_key = ANY(@tenants) AND item_id = @item ORDER BY tenant_key
            """ + (forUpdate ? " FOR UPDATE" : string.Empty), connection, transaction);
        command.Parameters.AddWithValue("tenants", tenants);
        command.Parameters.AddWithValue("item", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        string? owner = null;
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rowOwner = reader.GetString(0);
            if (owner is not null && !string.Equals(owner, rowOwner, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Conflicting Portal sharing owners require reconciliation.");
            }
            owner = rowOwner;
            groups.UnionWith(reader.GetFieldValue<string[]>(1));
        }
        return owner is null ? null : (owner, groups.ToArray());
    }

    private TenantReadScope ReadScope()
    {
        var context = contextAccessor.HttpContext;
        if (context is null || !PortalResourceReadAuthorizer.TryGetTrustedTenant(context, out _))
        {
            throw new InvalidOperationException("A trusted request tenant is required for Portal sharing.");
        }

        return TenantReadScope.For(context.RequestServices.GetService<ITenantContext>());
    }

    private static string[] Normalize(IReadOnlyList<string> values)
        => values.Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
