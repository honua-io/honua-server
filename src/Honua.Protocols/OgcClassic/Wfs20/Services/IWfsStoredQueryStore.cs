// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

// Managed stored queries, shared by every node. A query id is unique within its scope ignoring case,
// and a scope holds at most MaxPerScope queries. Definitions are opaque JSON documents to the store.
internal interface IWfsStoredQueryStore
{
    const int MaxPerScope = 1000;

    Task<IReadOnlyList<string>> ListAsync(string scope, CancellationToken cancellationToken);

    Task<string?> GetAsync(WfsStoredQueryKey key, CancellationToken cancellationToken);

    Task<WfsStoredQueryCreateResult> TryCreateAsync(WfsStoredQueryEntry entry, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(WfsStoredQueryKey key, CancellationToken cancellationToken);
}

internal readonly record struct WfsStoredQueryKey(string Scope, string QueryId);

internal sealed record WfsStoredQueryEntry(WfsStoredQueryKey Key, string Definition);

internal enum WfsStoredQueryCreateResult
{
    Created,
    DuplicateId,
    ScopeFull
}
