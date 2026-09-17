// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon;
using Amazon.Runtime;

namespace Honua.Cloud.Aws.Features;

/// <summary>
/// Applies Honua's region and optional endpoint override to an AWS SDK client configuration.
/// </summary>
internal static class AwsClientEndpoint
{
    /// <summary>
    /// Sets the region and, when supplied, the explicit service endpoint of <paramref name="config"/>.
    /// </summary>
    /// <remarks>
    /// The SDK treats <see cref="ClientConfig.ServiceURL"/> and <see cref="ClientConfig.RegionEndpoint"/>
    /// as mutually exclusive: assigning the endpoint clears the region. Without a signing region the
    /// SDK falls back to the ambient chain (AWS_REGION, the shared profile, then EC2 instance metadata),
    /// and in a container with none of those every request waits on 169.254.169.254, which a Docker
    /// bridge drops silently, so the request never leaves the process (#4998). The configured region is
    /// therefore pinned as the authentication region whenever an endpoint override is in effect.
    /// </remarks>
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
