// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Redshift.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Amazon Redshift is a read-only feature provider in this slice (#1712), so it does not support
/// feature-mutation writes and therefore does not require a transactional outbox. Reports a clear
/// capability limitation at startup so operators understand the durability tradeoff.
/// </summary>
/// <remarks>
/// Redshift is an analytical warehouse without the transactional throughput profile a mutation
/// outbox needs; write support is out of scope for the read-only slice.
/// </remarks>
internal sealed class RedshiftOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    public bool SupportsTransactionalOutbox => false;

    public string? CapabilityLimitationDescription =>
        "Redshift provider is read-only in this slice; feature mutations are not supported and no transactional outbox is required.";
}
