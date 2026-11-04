// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Db.DuckDB.Features.Infrastructure.Events.Outbox;

internal sealed class DuckDbOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    public bool SupportsTransactionalOutbox => false;

    public string? CapabilityLimitationDescription =>
        "DuckDB provider is read-only; feature mutations are not supported and no transactional outbox is required.";
}
