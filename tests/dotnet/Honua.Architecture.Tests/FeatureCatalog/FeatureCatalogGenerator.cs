// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Capabilities;
using Honua.Server;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

internal static class FeatureCatalogGenerator
{
    public const string SchemaVersion = "1.1.0";

    public const string MaturityImplemented = "implemented";

    public const string MaturityExperimental = "experimental";

    public const string MaturityPreview = "preview";

    private static readonly CapabilityRegistry Registry = new CapabilityRegistry();

    public static FeatureCatalog Generate()
    {
        var provingTestsByEndpoint = CollectProvingTests();
        var ledger = ProofLedgerProjection.Load();
        var capabilityMapper = CapabilityRouteMapper.Load();
        var maturityOverrides = CapabilityMaturityOverrides.Load();

        var entries = EndpointRegistry.All
            // EndpointRegistry.All may list the same (method, route) twice when a
            // route is grouped under two comment sections (e.g. the forms-package
            // generate route). The existing governance tests dedupe via HashSets;
            // the catalog projects one entry per distinct route so ids stay unique.
            .DistinctBy(endpoint => EndpointKey.Format(endpoint.Method, endpoint.Path), StringComparer.OrdinalIgnoreCase)
            .Select(endpoint =>
            {
                var key = EndpointKey.Format(endpoint.Method, endpoint.Path);
                var provingTests = provingTestsByEndpoint.TryGetValue(key, out var tests)
                    ? tests.OrderBy(value => value, StringComparer.Ordinal).ToArray()
                    : [];

                var surface = ledger.ResolveSurface(endpoint.Path);
                var id = SlugFor(endpoint.Method, endpoint.Path);
                var family = surface?.Protocol ?? "uncategorized";
                var method = endpoint.Method.ToUpperInvariant();

                var capability = capabilityMapper.Resolve(family, endpoint.Path, method);

                return new FeatureCatalogEntry
                {
                    Id = id,
                    Route = endpoint.Path,
                    Method = method,
                    Family = family,
                    Protocol = surface?.SurfaceKind ?? "http-route",
                    CodeLocation = surface?.CodeLocation ?? "src/Honua.Server/EndpointRegistry.cs",
                    ProvingTests = provingTests,
                    ProofLedgerSurface = surface?.SurfaceId ?? string.Empty,
                    Maturity = maturityOverrides.ResolveEffective(
                        capability,
                        ResolveMaturity(surface?.SurfaceId, id, endpoint.Path)),
                    Capability = capability
                };
            })
            .OrderBy(entry => entry.Method, StringComparer.Ordinal)
            .ThenBy(entry => entry.Route, StringComparer.Ordinal)
            .ToArray();

        return new FeatureCatalog
        {
            SchemaVersion = SchemaVersion,
            Generator = "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/FeatureCatalogGenerator.cs",
            TrackingIssue = "#1946",
            Slice = "slice-1-api-surface",
            Entries = entries
        };
    }

    public static string Serialize(FeatureCatalog catalog)
        => JsonSerializer.Serialize(catalog, FeatureCatalogJsonContext.Default.FeatureCatalog)
            .ReplaceLineEndings("\n") + "\n";

    private static Dictionary<string, HashSet<string>> CollectProvingTests()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var method in ArchitectureTestHelpers.IntegrationTestMethods()
                     .Where(static method => !IsUnconditionallySkipped(method)))
        {
            var testId = $"{method.DeclaringType?.FullName}.{method.Name}";
            foreach (var key in method
                         .GetCustomAttributes(typeof(EndpointAttribute), inherit: true)
                         .Cast<EndpointAttribute>()
                         .Select(endpointAttribute => EndpointKey.Normalize(endpointAttribute.Endpoint)))
            {
                if (key is null)
                {
                    continue;
                }

                if (!map.TryGetValue(key, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    map[key] = ids;
                }

                ids.Add(testId);
            }
        }

        return map;
    }

    private static bool IsUnconditionallySkipped(MethodInfo method)
        => method.GetCustomAttributes(inherit: true)
            .OfType<FactAttribute>()
            .Any(static attribute => !string.IsNullOrWhiteSpace(attribute.Skip));

    private static string SlugFor(string method, string path)
    {
        var raw = $"{method}{path}".ToLowerInvariant();
        var chars = raw
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    private static string ResolveMaturity(string? proofLedgerSurfaceId, string entryId, string route)
    {
        var routeDescriptorId = ResolveDescriptorIdForRoute(route);
        var descriptor =
            (string.IsNullOrEmpty(proofLedgerSurfaceId) ? null : Registry.Find(proofLedgerSurfaceId))
            ?? Registry.Find(entryId)
            ?? (routeDescriptorId is null ? null : Registry.Find(routeDescriptorId));

        return descriptor is null ? MaturityImplemented : MaturityLabel(descriptor.Maturity);
    }

    internal static string? ResolveDescriptorIdForRoute(string route)
    {
        // Lifecycle-only Preview declarations also cover the MapServer WMTS aliases,
        // whose capability key belongs to the otherwise GA MapServer family.
        if (route.Contains("/WMTS", StringComparison.OrdinalIgnoreCase))
        {
            return "serve.wmts";
        }

        if (route.Contains("/ImageServer", StringComparison.OrdinalIgnoreCase))
        {
            return "serve.geoservices-imageserver";
        }

        if (route.StartsWith("/ogc/coverages", StringComparison.OrdinalIgnoreCase))
        {
            return "serve.ogc-api-coverages";
        }

        // Temporal analytics — /api/v1/temporal/* was promoted to GA (Implemented) in
        // #2429 (temporal.filtering/extent-discovery/histogram/time-series-tiles), so it is
        // no longer a flipped experimental group: its routes fall through to the in-release
        // surface (implemented) like any other GA capability. Edition entitlements
        // (Community vs Pro) still apply, but those are not an experimental-maturity concern.

        // Geofence alerting is registered only when its Preview capability flag is enabled.
        if (route.StartsWith("/api/v1/admin/alerts/", StringComparison.OrdinalIgnoreCase))
        {
            return "alerts.geofence";
        }

        // Native mTLS (client certificates) — /api/v1/admin/security/client-certificates/* was
        // promoted to GA (Implemented) in #2431, then DEMOTED back to experimental in #2958
        // (release-safety follow-up to #2946/#2431): the always-on client-certificate scheme/RBAC
        // layer interposed on admin requests regardless of bearer-token validity. It is a flipped
        // experimental group again.
        if (route.StartsWith(
            "/api/v1/admin/security/client-certificates/", StringComparison.OrdinalIgnoreCase))
        {
            return "security.mtls";
        }

        // Disconnected-sync replica routes are opt-in Preview and carry the same gate as the
        // runtime endpoints in FeatureServerEndpoints.cs.
        if (route.StartsWith("/rest/services/", StringComparison.OrdinalIgnoreCase)
            && route.Contains("/FeatureServer/", StringComparison.OrdinalIgnoreCase)
            && (route.Contains("/replicas", StringComparison.OrdinalIgnoreCase)
                || route.EndsWith("/createReplica", StringComparison.OrdinalIgnoreCase)
                || route.EndsWith("/extractChanges", StringComparison.OrdinalIgnoreCase)
                || route.EndsWith("/synchronizeReplica", StringComparison.OrdinalIgnoreCase)
                || route.EndsWith("/unRegisterReplica", StringComparison.OrdinalIgnoreCase)))
        {
            return "sync.offline";
        }

        // Scene ingest is built-experimental and registered only when its named flag is enabled.
        if (route.StartsWith("/api/v1/admin/scenes/ingest/citygml", StringComparison.OrdinalIgnoreCase))
        {
            return "scene.bim-ingest";
        }

        if (route.StartsWith("/api/v1/admin/scenes/ingest/pointcloud", StringComparison.OrdinalIgnoreCase))
        {
            return "scene.pointcloud-ingest";
        }

        // Hosted 3D Tiles routes are built-experimental and share the serve.3d-tiles-scene flag.
        if (route.StartsWith("/scenes/", StringComparison.OrdinalIgnoreCase)
            && !route.Contains("/SceneServer", StringComparison.OrdinalIgnoreCase))
        {
            return "serve.3d-tiles-scene";
        }

        if (route.StartsWith("/api/v1/admin/scenes/generate", StringComparison.OrdinalIgnoreCase))
        {
            return "serve.3d-tiles-scene";
        }

        // Realtime feature streaming remains opt-in Preview until exact-candidate
        // transport qualification passes (#3810).
        if (route.StartsWith("/api/v1/streaming/features", StringComparison.OrdinalIgnoreCase) ||
            route.StartsWith("/api/v1/admin/streaming/features", StringComparison.OrdinalIgnoreCase))
        {
            return "realtime.feature-streams";
        }

        // SensorThings is registered only when its Preview flag is enabled.
        if (route.StartsWith("/sta/v1.1", StringComparison.OrdinalIgnoreCase))
        {
            return "serve.sensorthings";
        }

        // OGC API - EDR is Preview in release 2026.1; remaining functional
        // query corrections are deferred to release/2026.2.
        if (route.StartsWith("/edr", StringComparison.OrdinalIgnoreCase))
        {
            return "serve.ogc-api-edr";
        }

        // Branch versioning (VMS REST surface) — /rest/services/{serviceId}/VersionManagementServer/*
        // Gated Preview in the BH6-001/BH6-002 fix batch (ADR-0058).
        if (route.StartsWith("/rest/services/", StringComparison.OrdinalIgnoreCase) &&
            route.Contains("/VersionManagementServer", StringComparison.OrdinalIgnoreCase))
        {
            return "versioning.branch";
        }

        return null;
    }

    private static string MaturityLabel(CapabilityMaturity maturity)
        => maturity switch
        {
            CapabilityMaturity.Internal => "internal",
            CapabilityMaturity.Planned => "planned",
            CapabilityMaturity.Deferred => "deferred",
            CapabilityMaturity.Experimental => "experimental",
            CapabilityMaturity.Preview => MaturityPreview,
            CapabilityMaturity.Partial => "partial",
            CapabilityMaturity.Implemented => MaturityImplemented,
            _ => MaturityImplemented,
        };
}

internal static class EndpointKey
{
    public static string Format(string method, string path)
    {
        var normalizedPath = path.Trim();
        if (!normalizedPath.StartsWith('/'))
        {
            normalizedPath = "/" + normalizedPath;
        }

        return $"{method.Trim().ToUpperInvariant()} {normalizedPath}";
    }

    public static string? Normalize(string endpoint)
    {
        var parts = endpoint.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length != 2 ? null : Format(parts[0], parts[1]);
    }
}

internal sealed class FeatureCatalog
{
    public string SchemaVersion { get; init; } = string.Empty;

    public string Generator { get; init; } = string.Empty;

    public string TrackingIssue { get; init; } = string.Empty;

    public string Slice { get; init; } = string.Empty;

    public FeatureCatalogEntry[] Entries { get; init; } = [];
}

internal sealed class FeatureCatalogEntry
{
    public string Id { get; init; } = string.Empty;

    public string Route { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Family { get; init; } = string.Empty;

    public string Protocol { get; init; } = string.Empty;

    public string CodeLocation { get; init; } = string.Empty;

    public string[] ProvingTests { get; init; } = [];

    public string ProofLedgerSurface { get; init; } = string.Empty;

    public string Maturity { get; init; } = string.Empty;

    public string Capability { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(FeatureCatalog))]
internal sealed partial class FeatureCatalogJsonContext : JsonSerializerContext
{
}
