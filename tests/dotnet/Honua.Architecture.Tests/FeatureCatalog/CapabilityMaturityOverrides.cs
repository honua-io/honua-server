// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Capabilities;

namespace Honua.Architecture.Tests.FeatureCatalog;

internal sealed class CapabilityMaturityOverrides
{
    private readonly Dictionary<string, CapabilityMaturityOverrideRow> _byCapability;

    private CapabilityMaturityOverrides(IReadOnlyList<CapabilityMaturityOverrideRow> rows)
    {
        Overrides = rows;
        _byCapability = rows.ToDictionary(row => row.Capability, StringComparer.Ordinal);
    }

    public const string RelativePath = "docs/gis/data/capability-maturity-overrides.v1.json";

    public IReadOnlyList<CapabilityMaturityOverrideRow> Overrides { get; }

    public static CapabilityMaturityOverrides Load()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "capability-maturity-overrides.v1.json");

        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize(stream, CapabilityMaturityOverrideJsonContext.Default.CapabilityMaturityOverrideDocument)
            ?? throw new InvalidOperationException("Unable to deserialize capability-maturity-overrides.v1.json.");

        foreach (var row in document.Overrides)
        {
            if (ParseTier(row.Maturity) is null)
            {
                throw new InvalidOperationException(
                    $"capability-maturity-overrides.v1.json row '{row.Capability}' names an unknown maturity tier " +
                    $"'{row.Maturity}'. Valid tiers are the lower-case CapabilityMaturity names.");
            }
        }

        return new CapabilityMaturityOverrides(document.Overrides);
    }

    public string ResolveEffective(string capability, string registryMaturity)
    {
        if (!_byCapability.TryGetValue(capability, out var row))
        {
            return registryMaturity;
        }

        var overrideTier = ParseTier(row.Maturity);
        var registryTier = ParseTier(registryMaturity);

        // An unrecognized registry tier is treated as the most mature so the override
        // still applies; the override tier is validated on load and is never null here.
        return overrideTier is { } demoted && (registryTier is null || demoted < registryTier)
            ? row.Maturity
            : registryMaturity;
    }

    internal static CapabilityMaturity? ParseTier(string tier) => tier switch
    {
        "internal" => CapabilityMaturity.Internal,
        "planned" => CapabilityMaturity.Planned,
        "deferred" => CapabilityMaturity.Deferred,
        "experimental" => CapabilityMaturity.Experimental,
        "preview" => CapabilityMaturity.Preview,
        "partial" => CapabilityMaturity.Partial,
        FeatureCatalogGenerator.MaturityImplemented => CapabilityMaturity.Implemented,
        _ => null,
    };
}

internal sealed class CapabilityMaturityOverrideDocument
{
    public string SchemaVersion { get; init; } = string.Empty;

    public CapabilityMaturityOverrideRow[] Overrides { get; init; } = [];
}

internal sealed class CapabilityMaturityOverrideRow
{
    public string Capability { get; init; } = string.Empty;

    public string Maturity { get; init; } = string.Empty;

    public string ReasonCode { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityMaturityOverrideDocument))]
internal sealed partial class CapabilityMaturityOverrideJsonContext : JsonSerializerContext
{
}
