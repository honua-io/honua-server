// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.MapTools;

internal static class StudioCatalogSourceIdentity
{
    internal static string Create(string serviceId, int serviceLayerIndex)
        => Honua.Core.Features.Studio.Domain.StudioCatalogSourceIdentity.Create(serviceId, serviceLayerIndex);

    internal static bool TryCreate(string serviceId, int serviceLayerIndex, out string catalogId)
        => Honua.Core.Features.Studio.Domain.StudioCatalogSourceIdentity.TryCreate(
            serviceId, serviceLayerIndex, out catalogId);
}
