// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Oracle.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Oracle is a read-only feature provider in this slice (#1252), so it does not support
/// feature-mutation writes and therefore does not require a transactional outbox. Reports a
/// clear capability limitation at startup so operators understand the durability tradeoff.
/// </summary>
/// <remarks>
/// When Oracle gains write support (out of scope for this slice — write-through to enterprise
/// geodatabases is governed by both technical and IP considerations), replace this provider
/// with a true Oracle outbox implementation.
/// </remarks>
internal sealed class OracleOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    public bool SupportsTransactionalOutbox => false;

    public string? CapabilityLimitationDescription =>
        "Oracle provider is read-only in this slice; feature mutations are not supported and no transactional outbox is required.";
}
