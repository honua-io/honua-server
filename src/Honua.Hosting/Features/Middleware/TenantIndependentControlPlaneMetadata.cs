// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Middleware;

public sealed class TenantIndependentControlPlaneMetadata
{
    public static TenantIndependentControlPlaneMetadata Instance { get; } = new();

    private TenantIndependentControlPlaneMetadata()
    {
    }
}
