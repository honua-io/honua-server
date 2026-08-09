// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.DuckDB.Features.Infrastructure.Events.Outbox;

/// <summary>
/// DuckDB is a read-only feature provider in v1, so it cannot write outbox rows for
/// feature mutations. The capability provider reports the limitation so startup logs
/// flag the gap and the readiness endpoint reflects that the provider falls through to
/// the post-commit publish + retry queue path used for non-outbox-capable backends.
/// </summary>
internal sealed class DuckDbOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    public bool SupportsTransactionalOutbox => false;

    public string? CapabilityLimitationDescription =>
        "DuckDB provider is read-only; feature mutations are not supported and no transactional outbox is required.";
}
