// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed class PostgresFeatureEditIdempotencyReceipts(
    NpgsqlDataSource dataSource,
    string? schemaName) : IFeatureEditIdempotencyReceipts
{
    private readonly string _table = SchemaSearchPath.QualifyTable("grpc_apply_edits_receipt", schemaName);

    public async ValueTask<FeatureEditReceiptReplay?> TryReadWithEvidenceAsync(
        string scope, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await PostgresGrpcApplyEditsReceipt.TryReadWithEvidenceAsync(
            new PostgresGrpcApplyEditsReceipt.EvidenceContext
            {
                Connection = connection,
                Table = _table,
                TimeoutSeconds = 30
            }, scope, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>?> TryReadAsync(string scope, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await PostgresGrpcApplyEditsReceipt.TryReadAsync(
            connection,
            _table,
            scope,
            commandTimeoutSeconds: 30,
            cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        return new ReadOnlyMemory<byte>(bytes);
    }
}
