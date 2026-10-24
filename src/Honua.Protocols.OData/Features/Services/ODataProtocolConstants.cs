// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.OData.Services;

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

        return service.Protocols.Any(protocol => string.Equals(protocol, ProtocolName, StringComparison.OrdinalIgnoreCase));
    }
}
