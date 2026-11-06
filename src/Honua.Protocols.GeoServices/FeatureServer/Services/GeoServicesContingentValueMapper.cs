// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal static class GeoServicesContingentValueMapper
{
    public static ContingentValuesDefinition? Map(
        int publicLayerId,
        IReadOnlyList<MetadataV2ContingentValueGroup> groups)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        return new ContingentValuesDefinition
        {
            Id = publicLayerId,
            FieldGroups = groups
                .Select(MapFieldGroup)
                .ToArray(),
        };
    }

    private static ContingentValueFieldGroup MapFieldGroup(MetadataV2ContingentValueGroup group)
        => new()
        {
            Name = group.Name,
            Restrictive = group.Restrictive,
            Fields = group.Fields.ToArray(),
            ContingentValues = group.ContingentValues
                .OrderBy(value => value.Id)
                .Select(MapRow)
                .ToArray(),
        };

    private static ContingentValueRow MapRow(MetadataV2ContingentValue value)
        => new()
        {
            Id = value.Id,
            SubtypeCode = value.SubtypeCode?.Clone(),
            Values = value.Values.ToDictionary(
                pair => pair.Key,
                pair => MapFieldValue(pair.Value),
                StringComparer.OrdinalIgnoreCase),
        };

    private static ContingentFieldValue MapFieldValue(MetadataV2ContingentFieldValue value)
        => new()
        {
            Type = value.Type,
            Code = value.Code?.Clone(),
            Range = value.Range?.Select(element => element.Clone()).ToArray(),
        };
}
