// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>Projects the existing Studio catalog identity; it grants no source access.</summary>
internal static class StudioCatalogSourceIdentity
{
    internal static string Create(string serviceId, int layerId)
        => Honua.Core.Features.Studio.Domain.StudioCatalogSourceIdentity.Create(serviceId, layerId);
}
