// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>Shares one explicitly owned connection across sequential mutation and audit leases.</summary>
internal static class PostgresMutationTransaction
{
    private static readonly AsyncLocal<Context?> Current = new();

    internal static bool TryBorrow(IAdoNetDatabaseConnectionProvider provider, out NpgsqlConnectionLease lease)
    {
        var context = Current.Value;
        if (context is null)
        {
            lease = default;
            return false;
        }

        if (!context.Active || !ReferenceEquals(provider, context.Provider))
        {
            throw new InvalidOperationException("An audited mutation cannot outlive its transaction or span database providers.");
        }

        lease = new NpgsqlConnectionLease(context.Connection, context.Transaction);
        return true;
    }

    internal static async ValueTask<T> ExecuteAsync<T>(
        IAdoNetDatabaseConnectionProvider provider,
        Func<ValueTask<T>> mutation,
        Func<T, bool> shouldCommit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(shouldCommit);
        if (Current.Value is not null)
        {
            throw new InvalidOperationException("An audited mutation transaction is already active.");
        }

        // Resolve the target, including any secure-registry metadata lookup, before
        // starting a transaction. No System.Transactions auto-enlistment is involved.
        await using var owner = await provider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await owner.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var context = new Context(provider, owner.Connection, transaction);
        Current.Value = context;
        try
        {
            var result = await mutation().ConfigureAwait(false);
            if (shouldCommit(result))
            {
                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            // Invalidate copies carried by child execution contexts before disposing
            // the owner; a late task must never fall back to an autocommitted write.
            context.Active = false;
            Current.Value = null;
        }
    }

    private sealed class Context(
        IAdoNetDatabaseConnectionProvider provider, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        internal IAdoNetDatabaseConnectionProvider Provider { get; } = provider;
        internal NpgsqlConnection Connection { get; } = connection;
        internal NpgsqlTransaction Transaction { get; } = transaction;
        internal bool Active { get; set; } = true;
    }
}
