// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>A generation conflict on an owner-authorized draft snapshot.</summary>
internal sealed class StudioDraftGenerationConflictException(long currentGeneration)
    : GeoprocessingPreconditionFailedException("Stale draft generation; refresh and retry.")
{
    public long CurrentGeneration { get; } = currentGeneration;
}
