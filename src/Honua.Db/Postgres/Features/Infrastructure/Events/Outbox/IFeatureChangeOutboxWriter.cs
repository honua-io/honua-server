// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Postgres.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Provider-internal atomic write path for the feature-change transactional outbox.
/// </summary>
/// <remarks>
/// This contract is intentionally kept out of <c>Honua.Core</c> because it takes
/// an ambient ADO.NET <see cref="DbConnection"/>/<see cref="DbTransaction"/> so the
/// outbox row commits atomically with the feature mutation. Only the mutation-capable
/// provider (PostgreSQL) participates in this path, and the only caller is the provider's
/// own feature data-access layer. Keeping it provider-internal avoids leaking
/// <see cref="System.Data.Common"/> types through the public Core abstraction
/// (<see cref="IFeatureChangeOutboxRepository"/>), which is consumed by the
/// transport-agnostic outbox dispatcher.
/// </remarks>
internal interface IFeatureChangeOutboxWriter
{
    /// <summary>
    /// Inserts an outbox row inside an existing connection + transaction so the outbox
    /// row commits atomically with the feature mutation. This is the only supported
    /// write path: the dispatcher's atomic-write contract requires the row to be
    /// committed in the same transaction as the mutation, with no auto-commit fallback.
    /// </summary>
    Task WriteOutboxRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        FeatureChangeOutboxEntry entry,
        CancellationToken cancellationToken);
}
