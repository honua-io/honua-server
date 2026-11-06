// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

// Values are ordered from least to most mature so callers can compare them.
public enum CapabilityMaturity
{
    Internal = -1,

    Planned = 0,

    Deferred = 1,

    Experimental = 2,

    Preview = 3,

    Partial = 4,

    Implemented = 5,
}
