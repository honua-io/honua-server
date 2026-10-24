// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Azure.Core;
using Azure.Identity;

namespace Honua.ControlPlane;

internal static class AzureControlPlaneCredential
{
    public static TokenCredential SharedDefault { get; } = new DefaultAzureCredential();
}
