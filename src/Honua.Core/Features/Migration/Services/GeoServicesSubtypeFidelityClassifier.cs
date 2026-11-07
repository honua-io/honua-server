// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

public static class GeoServicesSubtypeFidelityClassifier
{
    public const string UnsupportedConstructMetadataKey = "unsupportedConstruct";

    public const string UnsupportedDetailMetadataKey = "unsupportedDetail";

    public static GeoServicesSubtypeFidelityFinding Classify(
        JsonElement resourceElement,
        GeoServicesConstructCapabilityDescriptor descriptor)
        => Classify(GeoServicesSubtypeParser.Parse(resourceElement), descriptor);

    public static GeoServicesSubtypeFidelityFinding Classify(
        GeoServicesSubtypeParseResult result,
        GeoServicesConstructCapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (result.UnsupportedCode is not { Length: > 0 } code)
        {
            // Captured: the registry stays the authority for the supported tier.
            return new GeoServicesSubtypeFidelityFinding
            {
                AutomationStatus = descriptor.AutomationStatus,
                Code = descriptor.Code,
                Reason = descriptor.Reason,
                ManualSteps = descriptor.ManualSteps
            };
        }

        // Not captured. The tier comes from the registry fallback so the classifier and the
        // registry cannot disagree; the code and reason name the specific construct.
        var (reason, manualSteps) = UnsupportedConstructs.TryGetValue(code, out var named)
            ? named
            : (descriptor.UnsupportedReason ?? descriptor.Reason, descriptor.UnsupportedManualSteps);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UnsupportedConstructMetadataKey] = code
        };
        if (!string.IsNullOrWhiteSpace(result.UnsupportedDetail))
        {
            metadata[UnsupportedDetailMetadataKey] = result.UnsupportedDetail!;
        }

        return new GeoServicesSubtypeFidelityFinding
        {
            AutomationStatus = descriptor.UnsupportedAutomationStatus ?? MigrationFidelityAutomationStatuses.Unsupported,
            Code = code,
            Reason = reason,
            ManualSteps = manualSteps,
            Metadata = metadata
        };
    }

    private static readonly FrozenDictionary<string, (string Reason, string[] ManualSteps)> UnsupportedConstructs =
        new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            [ImportCompatibilityCodes.GeoServicesFeatureTypeTemplatesUnsupported] = (
                "A source feature type declares more than one editing template. The canonical model carries a "
                + "single prototype per type, so no feature type on this resource was captured and the target "
                + "layer carries no type-driven editing defaults.",
                ["Choose the editing template each feature type should publish with, or model the additional "
                 + "templates as separate feature types, before cutover."]),
            [ImportCompatibilityCodes.GeoServicesFeatureTypeDomainClearingUnsupported] = (
                "A source feature type explicitly clears a field domain. The canonical model expresses only "
                + "domain inheritance or replacement, so no feature type on this resource was captured and the "
                + "target layer carries no per-type domain overrides.",
                ["Replace the cleared domain with an explicit per-type domain, or accept the published field "
                 + "domain for that type, before cutover."]),
            [ImportCompatibilityCodes.GeoServicesFeatureTypeIdentityUnsupported] = (
                "A source feature type carries no scalar identifier or no name, so it cannot be projected onto a "
                + "canonical type entry and no feature type on this resource was captured.",
                ["Give every source feature type a scalar id and a name, or drop the unusable type, before cutover."])
        }.ToFrozenDictionary(StringComparer.Ordinal);
}

public sealed record GeoServicesSubtypeFidelityFinding
{
    public required string AutomationStatus { get; init; }

    public required string Code { get; init; }

    public required string Reason { get; init; }

    public IReadOnlyList<string> ManualSteps { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
