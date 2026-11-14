// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    internal async Task DrainPendingAttachmentCleanupAsync(CancellationToken cancellationToken)
    {
        var layers = new List<int>();
        await using (var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var tables = await ResolveTablesAsync(connection, null, true, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"SELECT DISTINCT layer_id FROM {tables.Cleanup} ORDER BY layer_id LIMIT 100", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                layers.Add(reader.GetInt32(0));
            }
        }
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DrainImportCleanupAsync(layer).ConfigureAwait(false);
        }
    }
}
