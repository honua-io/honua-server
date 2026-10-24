// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Db.Postgres.Features.Infrastructure.Events.Outbox;

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
