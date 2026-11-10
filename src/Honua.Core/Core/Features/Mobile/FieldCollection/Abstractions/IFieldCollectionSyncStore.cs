// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Core.Features.Mobile.FieldCollection.Abstractions;

public interface IFieldCollectionSyncStore
{
    Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default);

    Task<FieldCollectionSyncCursor> GetSyncCursorAsync(string clientId, CancellationToken cancellationToken = default);

    // The read does not advance the cursor, because the client acknowledges only after local persistence.
    Task<FieldCollectionChangesPage> GetChangesAsync(
        string clientId,
        long sinceGeneration,
        int limit,
        CancellationToken cancellationToken = default);

    // A cursor past the committed watermark is rejected so a client cannot store a future generation.
    Task RecordSyncCursorAsync(
        string clientId,
        long lastSyncGeneration,
        CancellationToken cancellationToken = default);

    // Idempotency includes the client id, because devices that share one API key can mint the same change id.
    Task<FieldCollectionPushResult> PushChangeAsync(
        string clientId,
        FieldCollectionPushRequest request,
        CancellationToken cancellationToken = default);
}
