// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Collaboration;

// Every collaboration surface normalizes the map id before using it as a state key, because authorization accepts every GUID form but the logs key on the raw string.
internal static class SavedMapCollaborationMapId
{
    public static string Normalize(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        return Guid.TryParse(mapId, out var parsed) ? parsed.ToString("D") : mapId;
    }
}
