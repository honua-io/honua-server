// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Realtime;
using Npgsql;

namespace Honua.Server.Features.Realtime;

internal sealed class RealtimeTransactionWriter(PostgresRealtimeEventLog log, RealtimeDispatcher dispatcher)
    : IRealtimeTransactionWriter
{
    public async Task AppendAsync(RealtimeFamily family, IReadOnlyList<RealtimeTransactionEntry> entries,
        DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not NpgsqlTransaction postgres)
        {
            throw new ArgumentException("The realtime log joins PostgreSQL transactions only.", nameof(transaction));
        }

        await log.AppendAsync(family.Name,
            entries.Select(entry => new RealtimeLogRow(entry.ScopeKey, entry.Kind, entry.Payload)).ToArray(),
            postgres, cancellationToken).ConfigureAwait(false);
    }

    public void NotifyCommitted() => dispatcher.Wake();
}
