// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

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
