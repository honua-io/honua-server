// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon;
using Amazon.Runtime;

namespace Honua.Cloud.Aws.Features;

internal static class AwsClientEndpoint
{
    // An endpoint override clears the SDK region, so the configured region is pinned for signing; otherwise the call waits on instance metadata and never leaves the process.
    public static void Apply(ClientConfig config, string? region, string? serviceUrl)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            config.ServiceURL = serviceUrl;
            if (!string.IsNullOrWhiteSpace(region))
            {
                config.AuthenticationRegion = region;
            }
        }
    }
}
