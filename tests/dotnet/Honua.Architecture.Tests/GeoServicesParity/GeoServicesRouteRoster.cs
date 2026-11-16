// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Architecture.Tests.FeatureCatalog;

namespace Honua.Architecture.Tests.GeoServicesParity;

internal static class GeoServicesRouteRoster
{
    private static readonly Regex RouteConstraintRegex = new(
        @"\{(?<name>[^{}:]+):[^{}]+\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const string PortalSharingServiceType = "PortalSharing";

    public const string CatalogServiceType = "Catalog";

    public static readonly IReadOnlyDictionary<string, string> ExcludedServiceTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SceneServer"] =
                "3D scene layers are specified by the OGC I3S standard, not the GeoServices REST API; "
                + "they are addressed under /rest/services for client compatibility only and are tracked "
                + "by the I3S capability family rather than this matrix.",
        };

    public static readonly IReadOnlyDictionary<string, string> ServiceIdByServiceType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FeatureServer"] = "feature-server",
            ["MapServer"] = "map-server",
            ["ImageServer"] = "image-server",
            ["GeometryServer"] = "geometry-service",
            ["GPServer"] = "gp-server",
            ["GeocodeServer"] = "geocode-server",
            ["NAServer"] = "na-server",
            ["VectorTileServer"] = "vector-tile-server",
            ["VersionManagementServer"] = "version-management-server",
            [PortalSharingServiceType] = "portal-sharing",
            [CatalogServiceType] = "geoservices-catalog",
        };

    public static GeoServicesRoster Derive()
    {
        var operations = new Dictionary<string, List<(string Method, string Route, string Maturity)>>(StringComparer.Ordinal);
        var serviceTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmapped = new List<string>();
        var excludedHits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in FeatureCatalogGenerator.Generate().Entries)
        {
            if (!IsGeoServicesRoute(entry.Route))
            {
                continue;
            }

            var normalized = Normalize(entry.Route);
            if (normalized is null)
            {
                unmapped.Add(EndpointKey.Format(entry.Method, entry.Route));
                continue;
            }

            var (serviceType, geoServicesPath) = normalized.Value;

            if (ExcludedServiceTypes.ContainsKey(serviceType))
            {
                excludedHits.Add(serviceType);
                continue;
            }

            if (!operations.TryGetValue(geoServicesPath, out var routes))
            {
                routes = [];
                operations[geoServicesPath] = routes;
                serviceTypes[geoServicesPath] = serviceType;
            }

            routes.Add((entry.Method, entry.Route, entry.Maturity));
        }

        var derived = operations
            .Select(pair => new GeoServicesOperation
            {
                GeoServicesPath = pair.Key,
                ServiceType = serviceTypes[pair.Key],
                HonuaEndpoints = pair.Value
                    .Select(route => EndpointKey.Format(route.Method, route.Route))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                Maturity = ResolveMaturity(pair.Value.Select(route => route.Maturity)),
            })
            .OrderBy(operation => operation.GeoServicesPath, StringComparer.Ordinal)
            .ToArray();

        return new GeoServicesRoster
        {
            Operations = derived,
            Unmapped = unmapped.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            MatchedExclusions = excludedHits.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
        };
    }

    private static bool IsGeoServicesRoute(string route)
        => route.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase)
            || route.StartsWith("/sharing/rest/", StringComparison.OrdinalIgnoreCase);

    public static (string ServiceType, string GeoServicesPath)? Normalize(string route)
    {
        var path = RouteConstraintRegex.Replace(route.Trim(), "{${name}}");

        if (path.StartsWith("/sharing/rest/", StringComparison.OrdinalIgnoreCase))
        {
            return (PortalSharingServiceType, path);
        }

        if (string.Equals(path, "/rest/info", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/rest/services", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/rest/services/{folderName}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/rest/services/{folderName}/{serviceId}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/rest/services/Utilities/PrintingTools", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/rest/admin/{serviceName}.{serviceType}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/rest/admin/{folderName}/{serviceName}.{serviceType}", StringComparison.OrdinalIgnoreCase))
        {
            return (CatalogServiceType, path);
        }

        const string ServicesPrefix = "/rest/services/";
        if (!path.StartsWith(ServicesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = path[ServicesPrefix.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The service address is every segment before the first one ending in
        // "Server"; dropping it is the whole normalization (see the type remarks).
        var serviceTypeIndex = Array.FindIndex(
            segments,
            segment => segment.EndsWith("Server", StringComparison.Ordinal));

        return serviceTypeIndex < 0
            ? null
            : (segments[serviceTypeIndex], "/" + string.Join('/', segments[serviceTypeIndex..]));
    }

    private static string ResolveMaturity(IEnumerable<string> maturities)
    {
        var distinct = maturities.Distinct(StringComparer.Ordinal).ToArray();
        return Array.Find(distinct, value => !string.Equals(value, FeatureCatalogGenerator.MaturityImplemented, StringComparison.Ordinal))
            ?? FeatureCatalogGenerator.MaturityImplemented;
    }
}

internal sealed class GeoServicesOperation
{
    public required string GeoServicesPath { get; init; }

    public required string ServiceType { get; init; }

    public required string[] HonuaEndpoints { get; init; }

    public required string Maturity { get; init; }
}

internal sealed class GeoServicesRoster
{
    public required GeoServicesOperation[] Operations { get; init; }

    public required string[] Unmapped { get; init; }

    public required string[] MatchedExclusions { get; init; }
}
