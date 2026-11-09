// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

public enum ComplianceControlStatus
{
    Unknown = 0,

    Implemented = 1,

    PartiallyImplemented = 2,

    NotImplemented = 3,

    NotApplicable = 4,
}
