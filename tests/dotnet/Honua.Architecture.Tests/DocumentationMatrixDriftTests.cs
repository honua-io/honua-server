// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards documentation matrices whose mechanical route, parameter, and evidence claims
/// are projections of generated repository artifacts.
/// </summary>
public sealed class DocumentationMatrixDriftTests
{
    private static readonly Regex RouteParameterPattern = new(@"\{[^}]+\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MatrixRoutePattern = new(@"(?<methods>GET(?:/POST)?|POST) (?<route>/rest/services/[^`, |]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MatrixIdPattern = new(@"^\| (?<id>(?:CERT|JS|EL|DSK|CLI|BI)-[A-Z0-9-]*\d) \|", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [ArchitectureTest]
    public void MetadataCatalogImplementedRoutes_AreGeneratedFeatureCatalogRoutes()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var catalogRoutes = ReadFeatureCatalogRoutes(root);
        using var inventory = ReadJson(root, "docs", "internal", "developer", "metadata-catalog-endpoints.v1.json");

        foreach (var entry in inventory.RootElement.GetProperty("entries").EnumerateArray())
        {
            var status = entry.GetProperty("status").GetString();
            var routes = entry.GetProperty("endpointPatterns").EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (string.Equals(status, "removed", StringComparison.Ordinal))
            {
                routes.Should().BeEmpty($"removed metadata entry {entry.GetProperty("id").GetString()} must not advertise routes");
                continue;
            }

            if (!string.Equals(status, "implemented", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var route in routes)
            {
                catalogRoutes.Should().Contain(NormalizeRouteClaim(route),
                    $"metadata entry {entry.GetProperty("id").GetString()} must be derived from feature-catalog.json");
            }
        }
    }

    [ArchitectureTest]
    public void GeocodeServerMatrix_RoutesAndImplementedParametersMatchCode()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var servedRoutes = ReadFeatureCatalogRoutes(root)
            .Where(route => route.Contains("/rest/services/geocodeserver", StringComparison.Ordinal) ||
                            route.Contains("/rest/services/{}/geocodeserver", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var matrix = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "internal", "spikes", "geocode-server-matrix.md"));
        var documentedRoutes = MatrixRoutePattern.Matches(matrix)
            .SelectMany(match => ExpandMethods(match.Groups["methods"].Value, match.Groups["route"].Value))
            .Select(NormalizeRouteClaim)
            .ToHashSet(StringComparer.Ordinal);

        documentedRoutes.Should().BeEquivalentTo(servedRoutes,
            "the GeocodeServer route roster is derived from feature-catalog.json in both directions");

        var handler = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "src", "Honua.Server", "Features", "Geocoding", "GeocodingHandler.cs"));
        var implementedParameters = new[]
        {
            "singleLine", "maxLocations", "outSR", "countryCode", "countryCodes", "searchExtent", "magicKey", "category",
            "outFields", "location", "distance", "featureTypes", "langCode", "maxSuggestions", "addresses",
        };
        foreach (var parameter in implementedParameters)
        {
            handler.Should().Contain($"GetValue(values, \"{parameter}\")", $"the matrix marks {parameter} as implemented");
        }

        foreach (var parameter in new[] { "matchOutOfRange", "forStorage", "sourceCountry" })
        {
            handler.Should().NotContain($"GetValue(values, \"{parameter}\")", $"the matrix marks {parameter} as not implemented");
        }
    }

    [ArchitectureTest]
    public void ClientCertificationMatrix_IsJoinedToCommittedEvidence()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var source = ReadJson(root, "docs", "gis", "data", "client-certification-matrix.v1.json");
        var declaredIds = source.RootElement.GetProperty("testCases").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var markdown = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "CROSS_CLIENT_CERTIFICATION_MATRIX.md"));
        var documentedIds = MatrixIdPattern.Matches(markdown).Select(match => match.Groups["id"].Value).ToHashSet(StringComparer.Ordinal);
        declaredIds.Should().BeEquivalentTo(documentedIds, "the JSON vocabulary and published matrix must define the same IDs");

        var evidenceByLane = ReadCertificationEvidence(root);
        foreach (var lane in source.RootElement.GetProperty("lanes").EnumerateArray())
        {
            var laneId = lane.GetProperty("id").GetString()!;
            evidenceByLane.Should().ContainKey(laneId);
            var evidenceIds = evidenceByLane[laneId];
            var automated = ExpandPatterns(lane.GetProperty("automated"), declaredIds);
            var manual = lane.TryGetProperty("manual", out var manualElement) ? ExpandPatterns(manualElement, declaredIds) : [];
            var notApplicable = lane.TryGetProperty("notApplicable", out var naElement) ? ExpandPatterns(naElement, declaredIds) : [];

            evidenceIds.Should().BeSubsetOf(automated.Concat(manual).Concat(notApplicable),
                $"every committed {laneId} result must have declared applicability");
            automated.Should().BeSubsetOf(evidenceIds,
                $"every test declared automated for {laneId} must appear in committed evidence");
        }
    }

    private static HashSet<string> ReadFeatureCatalogRoutes(string root)
    {
        using var catalog = ReadJson(root, "docs", "gis", "data", "feature-catalog.json");
        return catalog.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => NormalizeRouteClaim($"{entry.GetProperty("method").GetString()} {entry.GetProperty("route").GetString()}"))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, HashSet<string>> ReadCertificationEvidence(string root)
    {
        var evidenceRoot = ArchitectureTestHelpers.CombinePath(root, "tests", "baselines", "client-compat");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(evidenceRoot, "*.cert.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var lane = document.RootElement.GetProperty("client_lane").GetString()!;
            if (!result.TryGetValue(lane, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                result.Add(lane, ids);
            }

            AddEvidenceIds(document.RootElement.GetProperty("results"), ids);
            AddEvidenceIds(document.RootElement.GetProperty("extensions"), ids);
        }

        return result;
    }

    private static void AddEvidenceIds(JsonElement items, HashSet<string> ids)
    {
        foreach (var item in items.EnumerateArray())
        {
            ids.Add(item.GetProperty("test_case_id").GetString()!);
        }
    }

    private static HashSet<string> ExpandPatterns(JsonElement patterns, HashSet<string> declaredIds)
        => patterns.EnumerateArray()
            .SelectMany(pattern => ExpandPattern(pattern.GetString()!, declaredIds))
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> ExpandPattern(string pattern, HashSet<string> declaredIds)
    {
        if (!pattern.EndsWith('*'))
        {
            return [pattern];
        }

        var prefix = pattern[..^1];
        return declaredIds.Where(id => id.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ExpandMethods(string methods, string route)
        => methods == "GET/POST" ? [$"GET {route}", $"POST {route}"] : [$"{methods} {route}"];

    private static string NormalizeRouteClaim(string claim)
    {
        var queryIndex = claim.IndexOf('?');
        var withoutQuery = queryIndex >= 0 ? claim[..queryIndex] : claim;
        return RouteParameterPattern.Replace(withoutQuery, "{}").TrimEnd('/').ToLowerInvariant();
    }

    private static JsonDocument ReadJson(string root, params string[] relativeSegments)
        => JsonDocument.Parse(File.ReadAllText(ArchitectureTestHelpers.CombinePath([root, .. relativeSegments])));
}
