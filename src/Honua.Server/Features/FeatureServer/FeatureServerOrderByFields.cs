// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Server.Features.FeatureServer;

internal static class FeatureServerOrderByFields
{
    internal static readonly FrozenSet<string> AllowedCoreOrderByFields = new[]
        {
            "objectid",
            "object_id",
            "created_at",
            "updated_at"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
