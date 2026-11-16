// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Architecture.Tests.FeatureCatalog;

internal sealed class CapabilityRouteMapper
{
    private readonly IReadOnlyList<CapabilityRouteRule> _rules;

    private CapabilityRouteMapper(IReadOnlyList<CapabilityRouteRule> rules) => _rules = rules;

    public const string RelativePath = "docs/gis/data/capability-route-mapping.v1.json";

    public static CapabilityRouteMapper Load()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "capability-route-mapping.v1.json");

        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize(stream, CapabilityRouteMappingJsonContext.Default.CapabilityRouteMappingDocument)
            ?? throw new InvalidOperationException("Unable to deserialize capability-route-mapping.v1.json.");

        return new CapabilityRouteMapper(document.Rules);
    }

    public IReadOnlyList<CapabilityRouteRule> Rules => _rules;

    public string Resolve(string family, string route, string method)
    {
        foreach (var rule in _rules)
        {
            if (!string.Equals(rule.Family, family, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.Method is { Length: > 0 } expectedMethod
                && !string.Equals(expectedMethod, method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.RoutePrefix is { Length: > 0 } prefix
                && !route.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.RouteContains is { Length: > 0 } substrings
                && !substrings.Any(substring => route.Contains(substring, StringComparison.Ordinal)))
            {
                continue;
            }

            return rule.Capability;
        }

        throw new InvalidOperationException(
            $"No capability-route-mapping rule matches {method} {route} (family '{family}'). " +
            $"Add a rule to {RelativePath} before regenerating the feature catalog.");
    }
}

internal sealed class CapabilityRouteMappingDocument
{
    public string SchemaVersion { get; init; } = string.Empty;

    public CapabilityRouteRule[] Rules { get; init; } = [];
}

internal sealed class CapabilityRouteRule
{
    public string Family { get; init; } = string.Empty;

    public string? Method { get; init; }

    public string? RoutePrefix { get; init; }

    public string[]? RouteContains { get; init; }

    public string Capability { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityRouteMappingDocument))]
internal sealed partial class CapabilityRouteMappingJsonContext : JsonSerializerContext
{
}
