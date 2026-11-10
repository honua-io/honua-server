// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Stac.Services;

internal static class StacProtocolConstants
{
    public const string ProtocolName = "Stac";

    public static bool IsEnabled(MetadataV2Service? service)
    {
        if (service is null)
        {
            return false;
        }

        return service.Protocols.Any(protocol =>
            string.Equals(protocol, ProtocolName, StringComparison.OrdinalIgnoreCase));
    }
}
