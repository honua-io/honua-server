// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Core;
using Azure.Identity;

namespace Honua.ControlPlane;

internal static class AzureControlPlaneCredential
{
    public static TokenCredential Default { get; } = new DefaultAzureCredential();
}
