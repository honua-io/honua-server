// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static class FeatureServerOrderByFields
{
    internal static readonly FrozenSet<string> AllowedCoreOrderByFields = new[]
        {
            FieldNames.ObjectId,
            "object_id",
            "created_at",
            "updated_at"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
