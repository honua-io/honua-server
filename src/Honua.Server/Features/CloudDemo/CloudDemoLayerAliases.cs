// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.CloudDemo;

internal static class CloudDemoLayerAliases
{
    public const int NaturalEarthLayer = 9000;
    public const int OpsIncidentsLayer = 9010;
    public const int OpsUnitTracksLayer = 9011;
    public const int OpsCoverageZonesLayer = 9012;
    public const int FieldInspectionsWritableLayer = 9020;
    public const int FieldInspectionsReadonlyLayer = 9021;
    public const int StoryAssetsLayer = 9030;
    public const int StoryRouteLayer = 9031;
    public const int StoryStopsLayer = 9032;

    public static readonly int[] AllLayerIds =
    [
        NaturalEarthLayer,
        OpsIncidentsLayer,
        OpsUnitTracksLayer,
        OpsCoverageZonesLayer,
        FieldInspectionsWritableLayer,
        FieldInspectionsReadonlyLayer,
        StoryAssetsLayer,
        StoryRouteLayer,
        StoryStopsLayer
    ];

    private static readonly Dictionary<string, IReadOnlyDictionary<int, int>> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["natural-earth"] = new Dictionary<int, int>
        {
            [0] = NaturalEarthLayer
        },
        ["ops-analytics"] = new Dictionary<int, int>
        {
            [0] = OpsIncidentsLayer,
            [1] = OpsUnitTracksLayer,
            [2] = OpsCoverageZonesLayer
        },
        ["field-inspections-demo-writable"] = new Dictionary<int, int>
        {
            [0] = FieldInspectionsWritableLayer
        },
        ["field-inspections-demo-readonly"] = new Dictionary<int, int>
        {
            [0] = FieldInspectionsReadonlyLayer
        }
    };

    public static bool TryResolve(string serviceId, int contractLayerId, out int physicalLayerId)
    {
        physicalLayerId = contractLayerId;
        if (!_aliases.TryGetValue(serviceId, out var layers) ||
            !layers.TryGetValue(contractLayerId, out physicalLayerId))
        {
            physicalLayerId = contractLayerId;
            return false;
        }

        return true;
    }

    public static bool TryRewriteFeatureServerPath(PathString path, out PathString rewrittenPath)
    {
        rewrittenPath = path;
        var value = path.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        const string prefix = "/rest/services/";
        const string featureServerSegment = "/FeatureServer/";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var serviceStart = prefix.Length;
        var featureServerStart = value.IndexOf(featureServerSegment, serviceStart, StringComparison.OrdinalIgnoreCase);
        if (featureServerStart < 0)
        {
            return false;
        }

        var serviceId = value[serviceStart..featureServerStart];
        var layerStart = featureServerStart + featureServerSegment.Length;
        if (layerStart >= value.Length)
        {
            return false;
        }

        var layerEnd = value.IndexOf('/', layerStart);
        var layerText = layerEnd < 0 ? value[layerStart..] : value[layerStart..layerEnd];
        if (!int.TryParse(layerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contractLayerId) ||
            !TryResolve(serviceId, contractLayerId, out var physicalLayerId) ||
            physicalLayerId == contractLayerId)
        {
            return false;
        }

        var remainder = layerEnd < 0 ? string.Empty : value[layerEnd..];
        rewrittenPath = new PathString(
            string.Concat(
                value.AsSpan(0, layerStart),
                physicalLayerId.ToString(CultureInfo.InvariantCulture),
                remainder));
        return true;
    }
}
