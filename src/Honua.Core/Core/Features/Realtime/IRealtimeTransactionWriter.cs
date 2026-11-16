// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Text.Json;

namespace Honua.Core.Features.Realtime;

// Appends a batch atomically with the caller's state transaction. The caller must notify only
// after commit so the local dispatcher cannot miss the newly visible events.
public interface IRealtimeTransactionWriter
{
    Task AppendAsync(RealtimeFamily family, IReadOnlyList<RealtimeTransactionEntry> entries,
        DbTransaction transaction, CancellationToken cancellationToken);

    void NotifyCommitted();
}

public readonly record struct RealtimeTransactionEntry(string ScopeKey, string Kind, JsonElement Payload);
