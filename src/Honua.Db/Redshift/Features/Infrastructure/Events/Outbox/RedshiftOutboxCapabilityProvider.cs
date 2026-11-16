// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Db.Redshift.Features.Infrastructure.Events.Outbox;

internal sealed class RedshiftOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    public bool SupportsTransactionalOutbox => false;

    public string? CapabilityLimitationDescription =>
        "Redshift provider is read-only in this slice; feature mutations are not supported and no transactional outbox is required.";
}
