// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Npgsql;

namespace Honua.Server.Features.Portal.Sharing;

/// <summary>
/// Reads membership and item audiences in one PostgreSQL statement. A revocation
/// committed before this statement is visible on every replica's next request.
/// </summary>
internal sealed class PostgresPortalAudienceSnapshotProvider(
    NpgsqlDataSource dataSource, string? schema = null) : IPortalAudienceSnapshotProvider
{
    private readonly string _groups = PortalSharingTables.Qualify("portal_community_groups", schema);
    private readonly string _shares = PortalSharingTables.Qualify("portal_item_shares", schema);

    public async Task<IReadOnlySet<string>> GetServiceIdsAsync(
        string username, string? tenantId, CancellationToken cancellationToken)
    {
        var services = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT DISTINCT s.item_id
            FROM {_shares} AS s
            JOIN {_groups} AS g ON g.tenant_key = s.tenant_key AND g.group_id = ANY(s.group_ids)
            WHERE s.tenant_key = @tenant
              AND (lower(g.owner_name) = lower(@username) OR EXISTS (
                SELECT 1 FROM unnest(g.members) AS m(member)
                WHERE lower(m.member) = lower(@username)))
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId ?? string.Empty);
        command.Parameters.AddWithValue("username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            services.Add(reader.GetString(0));
        }

        return services;
    }
}
