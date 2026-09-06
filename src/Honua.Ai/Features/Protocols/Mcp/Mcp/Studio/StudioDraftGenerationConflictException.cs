// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>A generation conflict on an owner-authorized draft snapshot.</summary>
internal sealed class StudioDraftGenerationConflictException(long currentGeneration)
    : GeoprocessingPreconditionFailedException("Stale draft generation; refresh and retry.")
{
    public long CurrentGeneration { get; } = currentGeneration;
}
