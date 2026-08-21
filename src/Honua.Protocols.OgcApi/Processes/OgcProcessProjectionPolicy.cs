// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// Defines the catalog boundary projected as directly executable OGC processes.
/// Source and sink connectors remain composition primitives for canonical plans;
/// every other built-in process is projected without maintaining a second allow-list.
/// </summary>
internal static class OgcProcessProjectionPolicy
{
    internal static bool IsProjectable(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return !string.Equals(definition.Category, "source", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(definition.Category, "sink", StringComparison.OrdinalIgnoreCase);
    }
}
