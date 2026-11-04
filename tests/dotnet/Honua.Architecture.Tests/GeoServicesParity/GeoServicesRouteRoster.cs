// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Architecture.Tests.FeatureCatalog;

namespace Honua.Architecture.Tests.GeoServicesParity;

internal static class GeoServicesRouteRoster
{
    /// <summary>Route-constraint stripper: <c>{layerId:int}</c> → <c>{layerId}</c>.</summary>
    private static readonly Regex RouteConstraintRegex = new(
        @"\{(?<name>[^{}:]+):[^{}]+\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Synthetic service type for <c>/sharing/rest/*</c>.</summary>
    public const string PortalSharingServiceType = "PortalSharing";

    /// <summary>Synthetic service type for the catalog roots (<c>/rest/info</c>, <c>/rest/services</c>).</summary>
    public const string CatalogServiceType = "Catalog";

    /// <summary>
    /// Esri service types served under <c>/rest/services/</c> that are deliberately
    /// outside the GeoServices REST parity matrix, each with the reason. The join gate
    /// asserts every exclusion still matches a served route, so an exclusion cannot
    /// quietly outlive the thing it excluded.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExcludedServiceTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SceneServer"] =
                "3D scene layers are specified by the OGC I3S standard, not the GeoServices REST API; "
                + "they are addressed under /rest/services for client compatibility only and are tracked "
                + "by the I3S capability family rather than this matrix.",
        };

    /// <summary>
    /// Maps a served Esri service type onto the parity matrix service that owns it.
    /// A served service type absent from this map fails the join gate: a new Esri
    /// service type has shipped and the matrix has no home for it (the
    /// <c>VectorTileServer</c>/<c>VersionManagementServer</c> case #2861 found).
    /// </summary>
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

    /// <summary>
    /// Projects the generated feature catalog onto the GeoServices operation roster.
    /// Consuming <see cref="FeatureCatalogGenerator.Generate"/> (rather than re-reading
    /// <c>EndpointRegistry.All</c> independently) is deliberate: it makes the parity
    /// roster a projection of the same generated capability data the catalog publishes,
    /// so the two cannot disagree about what is served or how mature it is.
    /// </summary>
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

    /// <summary>
    /// True for the route families the GeoServices REST parity matrix is answerable for:
    /// the Esri service tree (<c>/rest/services/*</c>), the REST catalog roots, and the
    /// ArcGIS portal-sharing surface. Scoping on the served path (rather than on the
    /// proof ledger's hand-maintained family labels) keeps the roster mechanical.
    /// </summary>
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
            || string.Equals(path, "/rest/services/Utilities/PrintingTools", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/rest/admin/{serviceName}.{serviceType}", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Folds the maturity of every route backing one operation onto a single tier.
    /// Any non-<c>implemented</c> tier wins: an operation is only as in-release as its
    /// least-available route, and reporting the optimistic tier would let a
    /// capability-gated (Preview) route be published as generally available.
    /// </summary>
    private static string ResolveMaturity(IEnumerable<string> maturities)
    {
        var distinct = maturities.Distinct(StringComparer.Ordinal).ToArray();
        return Array.Find(distinct, value => !string.Equals(value, FeatureCatalogGenerator.MaturityImplemented, StringComparison.Ordinal))
            ?? FeatureCatalogGenerator.MaturityImplemented;
    }
}

/// <summary>One served Esri operation, derived from the generated capability data.</summary>
internal sealed class GeoServicesOperation
{
    /// <summary>Esri-relative operation path; the join key between roster and judgement.</summary>
    public required string GeoServicesPath { get; init; }

    /// <summary>Esri service type that owns the operation (<c>FeatureServer</c>, …).</summary>
    public required string ServiceType { get; init; }

    public required string[] HonuaEndpoints { get; init; }

    /// <summary>Capability-registry maturity tier carried through from the feature catalog.</summary>
    public required string Maturity { get; init; }
}

/// <summary>The derived GeoServices roster plus what the derivation could not place.</summary>
internal sealed class GeoServicesRoster
{
    public required GeoServicesOperation[] Operations { get; init; }

    /// <summary>Served GeoServices-family routes that carry no recognizable service type.</summary>
    public required string[] Unmapped { get; init; }

    /// <summary>Excluded service types that actually matched a served route.</summary>
    public required string[] MatchedExclusions { get; init; }
}
