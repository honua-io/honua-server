// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public enum AuditEventType
{
    Authentication = 0,

    Authorization = 1,

    AdminAction = 2,

    ConfigChange = 3,

    DataExport = 4,

    DataDelete = 5,
}
