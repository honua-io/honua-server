// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Core.Features.MultiTenancy.Abstractions;

public interface ITenantUsageMeter
{
    long RecordRequest(string tenantId);

    IReadOnlyList<TenantUsageSnapshot> Snapshot();
}

public readonly record struct TenantUsageSnapshot(string TenantId, long RequestCount);
