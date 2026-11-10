// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public interface IAuditChainIntegritySignal
{
    bool HasVerified { get; }

    DateTimeOffset? LastVerifiedAt { get; }

    // Rows written before hashing are reported as unhashed, not as a broken link.
    bool IsChainBroken { get; }

    AuditIntegrityReport? LastReport { get; }
}
