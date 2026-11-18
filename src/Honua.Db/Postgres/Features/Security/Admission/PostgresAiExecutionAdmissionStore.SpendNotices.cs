// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Security.Admission;

internal sealed partial class PostgresAiExecutionAdmissionStore
{
    private sealed record SpendNoticeQuery<T>(string Sql, Func<NpgsqlDataReader, T> Read);

    private async Task<IReadOnlyList<T>> ListPendingNoticesAsync<T>(
        SpendNoticeQuery<T> query, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(query.Sql, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var notices = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            notices.Add(query.Read(reader));
        }
        return notices;
    }

    private async Task<bool> MarkNoticeQueuedAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        command.Connection = connection;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
