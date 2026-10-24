// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Authentication;
using Npgsql;

namespace Honua.Server.Features.Portal.Sharing;

/// <summary>Durable, tenant-scoped item audiences; every mutation locks its item row.</summary>
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
        var row = await ReadAsync(connection, null, TenantKey(), itemId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        return row is null ? PortalItemShareState.None : new PortalItemShareState(false, false, row.Value.GroupIds);
    }

    public async Task<string?> GetOwnerAsync(string itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadAsync(connection, null, TenantKey(), itemId, forUpdate: false, cancellationToken).ConfigureAwait(false);
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
        var tenant = TenantKey();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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

        var current = await ReadAsync(connection, transaction, tenant, itemId, forUpdate: true, cancellationToken).ConfigureAwait(false);
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
            UPDATE {_table} SET group_ids = @groups WHERE tenant_key = @tenant AND item_id = @item
            """, connection, transaction);
        update.Parameters.AddWithValue("groups", groups.ToArray());
        update.Parameters.AddWithValue("tenant", tenant);
        update.Parameters.AddWithValue("item", itemId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new PortalItemShareState(false, false, groups);
    }

    private async Task<(string Owner, string[] GroupIds)?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenant,
        string itemId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT owner_name, group_ids FROM {_table}
            WHERE tenant_key = @tenant AND item_id = @item
            """ + (forUpdate ? " FOR UPDATE" : string.Empty), connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("item", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetFieldValue<string[]>(1)) : null;
    }

    private string TenantKey()
    {
        var context = contextAccessor.HttpContext;
        if (context is null || !PortalResourceReadAuthorizer.TryGetTrustedTenant(context, out var tenantId))
        {
            throw new InvalidOperationException("A trusted request tenant is required for Portal sharing.");
        }

        return tenantId ?? string.Empty;
    }

    private static string[] Normalize(IReadOnlyList<string> values)
        => values.Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
