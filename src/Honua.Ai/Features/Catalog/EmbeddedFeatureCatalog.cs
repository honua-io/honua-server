// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Catalog;

// The one embedded copy of docs/gis/data/feature-catalog.json: the MCP feature catalog resource serves it
// and Honua.Server's deployment capability route catalog reads it.
internal static class EmbeddedFeatureCatalog
{
    internal const string ResourceName = "Honua.Ai.Catalog.feature-catalog.json";

    internal static Stream Open()
    {
        var assembly = typeof(EmbeddedFeatureCatalog).Assembly;
        return assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded feature catalog '{ResourceName}' was not found in {assembly.GetName().Name}.");
    }
}
