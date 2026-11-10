// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Domain;

public enum TenantStatus
{
    Active = 0,

    Suspended = 1,

    Deleted = 2,
}
