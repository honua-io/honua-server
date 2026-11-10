// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public enum AuditActorType
{
    Anonymous = 0,

    UserId = 1,

    ApiKey = 2,

    System = 3,
}
