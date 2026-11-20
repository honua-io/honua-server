// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Validation;

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

    // Hidden fields are left out of every public read path, so a caller's sort cannot name one,
    // even when it carries a core name such as created_at.
    internal static ImmutableArray<OrderByClause>? ParseCallerOrderBy(string? orderByFields, MetadataV2Object resource)
    {
        var hidden = resource.HiddenSchemaFieldNames();
        return hidden.Count == 0
            ? OrderByParsing.ParseFeatureServerOrderBy(orderByFields, resource, AllowedCoreOrderByFields)
            : OrderByParsing.ParseFeatureServerOrderBy(
                orderByFields,
                resource.WithoutHiddenSchemaFields(),
                AllowedCoreOrderByFields.Where(field => !hidden.Contains(field)).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}
