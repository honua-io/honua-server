// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Postgres.Features.Infrastructure.Events.Outbox;

/// <summary>
/// PostgreSQL supports a transactional outbox: rows can be written in the same
/// <c>DbTransaction</c> as a feature mutation, and dispatchers claim them with
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c> for safe multi-node behavior.
/// </summary>
internal sealed class PostgresOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    public bool SupportsTransactionalOutbox => true;

    public string? CapabilityLimitationDescription => null;
}
