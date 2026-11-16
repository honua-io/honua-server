// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.Caching;

// The factory stays unresolved until a request: database preflight and migrations run first.
internal sealed class PostgresExternalLayerRevisions(
    Func<JobStoreDatabase> databaseFactory, IHttpContextAccessor accessor) : IExternalLayerRevisionSource
{
    private static readonly object RequestKey = new();

    public Task<string> GetRevisionAsync(CancellationToken cancellationToken = default)
    {
        if (accessor.HttpContext is not { } context)
        {
            return ReadAsync(cancellationToken);
        }

        lock (context.Items)
        {
            if (context.Items.TryGetValue(RequestKey, out var cached))
            {
                return (Task<string>)cached!;
            }

            var revision = ReadAsync(cancellationToken);
            context.Items[RequestKey] = revision;
            return revision;
        }
    }

    private Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var database = databaseFactory();
        return database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT count(*), md5(COALESCE(string_agg(layer_id::text || chr(58) || revision::text, chr(44) ORDER BY layer_id), chr(48)))
                FROM (SELECT layer_id, revision FROM {database.Table("external_layer_revisions")} ORDER BY layer_id LIMIT 1025) revisions
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt64(0) > 1024)
            {
                throw new InvalidOperationException("External edit tracking supports at most 1024 layers per deployment.");
            }

            return reader.GetString(1);
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }
}
