// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.SqlServer.Features.Infrastructure.Events.Outbox;

/// <summary>
/// SQL Server is a read-only feature provider in this thin slice (#850), so it does
/// not support feature-mutation writes and therefore does not require a transactional
/// outbox. Reports a clear capability limitation at startup so operators understand
/// the durability tradeoff.
/// </summary>
/// <remarks>
/// When SQL Server gains write support, replace this provider with a true SQL Server
/// outbox implementation (table + multi-node-safe claim using <c>WITH (UPDLOCK, READPAST)</c>).
/// </remarks>
internal sealed class SqlServerOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    public bool SupportsTransactionalOutbox => false;

    public string? CapabilityLimitationDescription =>
        "SQL Server provider is read-only in this slice; feature mutations are not supported and no transactional outbox is required.";
}
