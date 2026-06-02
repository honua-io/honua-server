// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Maps the canonical <see cref="MetadataV2Subtypes"/> resource model onto the
/// Esri-style FeatureServer layer subtype surface. Reuses
/// <see cref="GeoServicesFieldDomainMapper"/> for per-subtype field domains so the
/// served domain shape stays consistent with the layer-level field domains.
/// </summary>
internal static class GeoServicesSubtypeMapper
{
    public static GeoServicesSubtypeInfo[]? Map(MetadataV2Subtypes? subtypes)
    {
        if (subtypes is null || subtypes.Subtypes.Count == 0)
        {
            return null;
        }

        return subtypes.Subtypes
            .Select(subtype => new GeoServicesSubtypeInfo
            {
                Code = subtype.Code.Clone(),
                Name = subtype.Name,
                DefaultValues = MapDefaultValues(subtype.FieldOverrides),
                Domains = MapDomains(subtype.FieldOverrides),
            })
            .ToArray();
    }

    private static Dictionary<string, JsonElement>? MapDefaultValues(
        IReadOnlyDictionary<string, MetadataV2SubtypeFieldOverride> overrides)
    {
        var defaults = overrides
            .Where(pair => pair.Value.DefaultValue is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.DefaultValue!.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);

        return defaults.Count == 0 ? null : defaults;
    }

    private static Dictionary<string, GeoServicesFieldDomainInfo>? MapDomains(
        IReadOnlyDictionary<string, MetadataV2SubtypeFieldOverride> overrides)
    {
        var domains = new Dictionary<string, GeoServicesFieldDomainInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides)
        {
            if (GeoServicesFieldDomainMapper.Map(pair.Value.Domain) is { } mapped)
            {
                domains[pair.Key] = mapped;
            }
        }

        return domains.Count == 0 ? null : domains;
    }
}
