// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    public async Task<GeoservicesImportResult?> RecoverCommittedImportAsync(
        GeoservicesImportRequest request, CancellationToken cancellationToken = default)
    {
        ValidateTableName(request.TableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.JobId);
        var schema = ResolveTargetSchema(request.TargetSchema);
        await using var lease = await OpenConnectionAsync(cancellationToken);
        await using var session = await _sessionLockConnections
            .OpenAsync(lease, cancellationToken, hasCallerTransaction: lease.Transaction is not null);
        var connection = session.Connection;
        if (!await TryAcquireTargetImportLockAsync(connection, schema, request.TableName, cancellationToken))
        {
            throw new InvalidOperationException("Import commit recovery must wait for the active target writer.");
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var context = new ImportCommitReceiptContext(connection, transaction, request, schema);
            return await ReadCommittedImportAsync(context, cancellationToken);
        }
        finally
        {
            await ReleaseTargetImportLockAsync(connection, schema, request.TableName);
        }
    }
}
