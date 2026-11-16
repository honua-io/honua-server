// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Metadata;

internal sealed class PublicPublicationPolicyWrite : IAsyncDisposable
{
    private readonly NpgsqlConnectionLease _connection;
    private readonly NpgsqlTransaction? _ownedTransaction;

    private PublicPublicationPolicyWrite(NpgsqlConnectionLease connection, NpgsqlTransaction? ownedTransaction)
    {
        _connection = connection;
        _ownedTransaction = ownedTransaction;
    }

    internal NpgsqlTransaction Transaction => _connection.Transaction ?? _ownedTransaction!;

    internal static async Task<PublicPublicationPolicyWrite> OpenAsync(
        IAdoNetDatabaseConnectionProvider provider, string? schemaName, CancellationToken cancellationToken)
    {
        await PublicPublicationWriteFence.PrepareWriteAsync(provider, schemaName, cancellationToken).ConfigureAwait(false);
        var connection = await provider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction? ownedTransaction = null;
        try
        {
            ownedTransaction = connection.Transaction is null
                ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
            var transaction = connection.Transaction ?? ownedTransaction!;
            await PublicPublicationWriteFence.LockAsync(transaction, cancellationToken).ConfigureAwait(false);
            await PublicPublicationWriteFence.RequireNoPublicCopiesAsync(transaction, schemaName, cancellationToken).ConfigureAwait(false);
            return new(connection, ownedTransaction);
        }
        catch
        {
            await new PublicPublicationPolicyWrite(connection, ownedTransaction).DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task CommitAsync(CancellationToken cancellationToken) => _ownedTransaction is null
        ? Task.CompletedTask : _ownedTransaction.CommitSafelyAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ownedTransaction is not null)
            {
                await _ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
