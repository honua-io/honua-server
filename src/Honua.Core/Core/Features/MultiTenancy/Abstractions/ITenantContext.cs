// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Abstractions;

// The context is per request and must not be cached, and handlers require a tenant before tenant-scoped reads.
public interface ITenantContext
{
    string? TenantId { get; }

    TenantContextSource Source { get; }

    // The explanation names the missing claim and does not include a tenant id.
    bool RequireTenantId(out string tenantId, out string? reason);
}

public enum TenantContextSource
{
    Anonymous = 0,

    Claim = 1,

    Header = 2,

    Default = 3,
}
