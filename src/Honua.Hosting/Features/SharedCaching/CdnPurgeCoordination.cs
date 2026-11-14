// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Coordination;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.SharedCaching;

// What a task needs to coordinate its purges with the request being handled and with the other tasks; each part is optional.
internal sealed class CdnPurgeCoordination
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public IHttpContextAccessor? HttpContextAccessor { get; init; }

    public IOptionsMonitor<SharedCacheOptions>? Options { get; init; }

    public ILeaseStore? LeaseStore { get; init; }
}
