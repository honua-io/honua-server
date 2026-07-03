// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Filtering;

/// <summary>
/// Shared defaults for resolving a resource's temporal ("datetime") field when no
/// temporal role is explicitly configured.
/// </summary>
internal static class TemporalFieldDefaults
{
    /// <summary>
    /// Well-known date/time attribute names, in priority order, used to populate a STAC Item's
    /// <c>datetime</c> and to resolve the temporal queryable when no temporal StartTimeField is
    /// configured. The STAC mapping, search, and CQL2 filter layers must share this list so the
    /// filter targets the same column the Item actually exposes.
    /// </summary>
    internal static readonly string[] TemporalFallbackFieldNames =
    [
        "datetime", "created_at", "updated_at", "start_datetime",
        "end_datetime", "timestamp", "event_date", "date"
    ];
}
