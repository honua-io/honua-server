// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Maps the canonical <see cref="MetadataV2ContingentValueGroup"/> resource model onto the
/// Esri-style FeatureServer <c>queryContingentValues</c> per-layer definition surface. Reuses the
/// same JSON-typed code/range value shape the field-domain and subtype surfaces use so the served
/// contingent values stay consistent with the layer-level domains they reference (#1878).
/// </summary>
internal static class GeoServicesContingentValueMapper
{
    /// <summary>
    /// Maps a layer's contingent value groups to a per-layer definition, or <c>null</c> when the
    /// layer declares no contingent values (so the caller can omit it from the response collection).
    /// </summary>
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
