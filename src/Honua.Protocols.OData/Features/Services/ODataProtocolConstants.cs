// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// OData protocol identifiers and metadata-v2 protocol checks local to the OData adapter.
/// </summary>
internal static class ODataProtocolConstants
{
    public const string ProtocolName = "OData";

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
