// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Filtering;

internal static class TemporalFieldDefaults
{
    // STAC mapping, search, and CQL2 share this list so a temporal filter targets the column the item exposes.
    internal static readonly string[] TemporalFallbackFieldNames =
    [
        "datetime", "created_at", "updated_at", "start_datetime",
        "end_datetime", "timestamp", "event_date", "date"
    ];
}
