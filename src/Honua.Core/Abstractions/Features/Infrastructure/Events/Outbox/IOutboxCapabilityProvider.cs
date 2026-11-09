// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

// Only PostgreSQL routes the canonical publish through the outbox; the other backends publish after commit.
public interface IOutboxCapabilityProvider
{
    bool SupportsTransactionalOutbox { get; }

    string? CapabilityLimitationDescription { get; }
}
