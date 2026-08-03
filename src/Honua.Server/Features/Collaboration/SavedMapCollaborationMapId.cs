// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Collaboration;

/// <summary>
/// Canonicalizes the saved-map collaboration <c>mapId</c> route value (honua-server#2999).
/// Authorization accepts every textual GUID form <see cref="Guid.TryParse(string?, out Guid)"/>
/// understands, but sessions, the op log, replay, and checkpoints key state off the raw string —
/// so an operation appended under the "N" form of a draft id and a checkpoint requested under the
/// equivalent "D" form would otherwise split into different logs. Every collaboration surface
/// must normalize the route value through this helper before using it as a state key.
/// </summary>
internal static class SavedMapCollaborationMapId
{
    /// <summary>
    /// Returns the canonical key for a collaboration map id: the "D"-formatted GUID when the
    /// value parses as one, otherwise the original string (non-GUID ids are opaque keys).
    /// </summary>
    public static string Normalize(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        return Guid.TryParse(mapId, out var parsed) ? parsed.ToString("D") : mapId;
    }
}
