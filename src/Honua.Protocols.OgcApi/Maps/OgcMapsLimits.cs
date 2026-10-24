// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Maps;

/// <summary>
/// Shared OGC API - Maps request limits.
/// </summary>
internal static class OgcMapsLimits
{
    /// <summary>
    /// Maximum number of collections allowed in a single dataset map request.
    /// </summary>
    public const int MaxCollectionsPerDatasetMapRequest = 100;
}
