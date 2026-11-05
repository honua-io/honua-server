// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

public enum DeploymentMode
{
    SingleInstance = 0,

    MultiNode = 1,

    Development = 2,

    Staging = 3,

    Production = 4
}
