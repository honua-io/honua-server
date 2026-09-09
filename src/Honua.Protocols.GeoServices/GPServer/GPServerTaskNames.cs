// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Esri-facing names for canonical processes, shared by REST and SOAP discovery.
/// The canonical IDs and existing routes remain unchanged.
/// </summary>
internal static class GPServerTaskNames
{
    internal static string GetEncodingPrefix(IReadOnlyList<ProcessDefinition> processes)
    {
        var prefix = "Honua_";
        // Keep generated routes outside every existing process-ID namespace.
        // This also preserves direct addressing of custom IDs that look encoded.
        while (processes.Any(process => process.ProcessId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            prefix += "_";
        }
        return prefix;
    }

    internal static string Encode(string processId, string prefix)
        // Both Esri Python clients generate function names from advertised names.
        // Hex preserves punctuation and case distinctions without keyword or
        // snake-case collisions; human-readable labels come from process titles.
        => prefix + Convert.ToHexString(Encoding.UTF8.GetBytes(processId));
}
