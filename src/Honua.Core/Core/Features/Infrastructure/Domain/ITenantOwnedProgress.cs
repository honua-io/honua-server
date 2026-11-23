// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

// Progress that records the tenant owning its operation, for operations that may have no durable job.
public interface ITenantOwnedProgress
{
    string? TenantId { get; }
}
