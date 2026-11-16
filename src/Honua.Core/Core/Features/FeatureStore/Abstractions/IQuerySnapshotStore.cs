// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IQuerySnapshotStore
{
    Task SaveAsync(Guid id, byte[] payload, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    Task<byte[]?> ReadAsync(Guid id, CancellationToken cancellationToken = default);
}
