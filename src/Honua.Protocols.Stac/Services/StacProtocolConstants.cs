// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Stac.Services;

/// <summary>
/// STAC protocol identifiers and metadata-v2 protocol checks local to the STAC adapter.
/// </summary>
internal static class StacProtocolConstants
{
    public const string ProtocolName = "Stac";

    public static bool IsEnabled(MetadataV2Service? service)
    {
        if (service is null)
        {
            return false;
        }

        foreach (var protocol in service.Protocols)
        {
            if (string.Equals(protocol, ProtocolName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
