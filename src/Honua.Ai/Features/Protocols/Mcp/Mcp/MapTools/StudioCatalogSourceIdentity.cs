// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>Projects the existing Studio catalog identity; it grants no source access.</summary>
internal static class StudioCatalogSourceIdentity
{
    internal static string Create(string serviceId, int layerId)
        => Honua.Core.Features.Studio.Domain.StudioCatalogSourceIdentity.Create(serviceId, layerId);

    internal static bool TryCreate(string serviceId, int layerId, out string catalogId)
        => Honua.Core.Features.Studio.Domain.StudioCatalogSourceIdentity.TryCreate(
            serviceId, layerId, out catalogId);
}
