// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geoprocessing;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// GeoServices compatibility facade over the canonical Esri task-name projection.
/// The registry lives in Honua.Geoprocessing so GPServer and AI/MCP cannot drift.
/// </summary>
internal static class GPServerEsriTaskAliases
{
    public static string? GetAlias(string processId)
        => EsriGpTaskProjection.GetAlias(processId);

    public static bool TryResolveProcessId(string taskName, out string processId)
        => EsriGpTaskProjection.TryResolveProcessId(taskName, out processId);
}
