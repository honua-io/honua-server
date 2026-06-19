// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Snowflake.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Snowflake is a read-only feature provider in this thin slice (#1713), so it does
/// not support feature-mutation writes and therefore does not require a transactional
/// outbox. Reports a clear capability limitation at startup so operators understand
/// the durability tradeoff.
/// </summary>
/// <remarks>
/// When Snowflake gains write support, replace this provider with a true Snowflake
/// outbox implementation. Snowflake is an analytical warehouse without row-level
/// claim locks; a durable outbox would need a different multi-node-safe dispatch
/// design than the relational <c>UPDLOCK, READPAST</c> pattern.
/// </remarks>
internal sealed class SnowflakeOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    /// <inheritdoc />
    public bool SupportsTransactionalOutbox => false;

    /// <inheritdoc />
    public string? CapabilityLimitationDescription =>
        "Snowflake provider is read-only in this slice; feature mutations are not supported and no transactional outbox is required.";
}
