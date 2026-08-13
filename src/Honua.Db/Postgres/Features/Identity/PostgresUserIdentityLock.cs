// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Identity;

/// <summary>
/// Serializes changes to a managed user's durable role projection with SCIM group
/// membership changes for the same identity. An advisory lock is used instead of a row
/// lock because SCIM groups may reference users before they are provisioned.
/// </summary>
internal static class PostgresUserIdentityLock
{
    private const string AcquireSql =
        "SELECT pg_advisory_xact_lock(hashtextextended(LOWER(@user_id), 0))";
    private const string AcquireGroupSql =
        "SELECT pg_advisory_xact_lock(hashtextextended('scim-group:' || LOWER(@group_id), 0))";

    public static async Task AcquireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AcquireSql, connection, transaction);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task AcquireManyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<string> userIds,
        CancellationToken cancellationToken)
    {
        var orderedUserIds = userIds
            .Where(static userId => !string.IsNullOrWhiteSpace(userId))
            .Select(static userId => userId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static userId => userId, StringComparer.OrdinalIgnoreCase);

        foreach (var userId in orderedUserIds)
        {
            await AcquireAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task AcquireGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string groupId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AcquireGroupSql, connection, transaction);
        command.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, groupId.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
