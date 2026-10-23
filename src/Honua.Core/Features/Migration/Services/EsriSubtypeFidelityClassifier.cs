// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Turns the shared <see cref="EsriSubtypeParser"/> outcome for one source resource into the
/// subtype fidelity finding the migration inventory records against that resource.
/// </summary>
/// <remarks>
/// The classifier is the single seam where "what the parser could capture" becomes "what the
/// operator is told". Keeping it next to the parser is what stops the two from disagreeing: a
/// source editing construct the canonical model cannot represent is captured by neither, and is
/// named by exactly one stable <see cref="ImportCompatibilityCodes"/> value in both.
/// </remarks>
public static class EsriSubtypeFidelityClassifier
{
    /// <summary>Metadata key carrying the stable code of the unsupported source construct.</summary>
    public const string UnsupportedConstructMetadataKey = "unsupportedConstruct";

    /// <summary>Metadata key carrying the operator-facing description of the offending source type.</summary>
    public const string UnsupportedDetailMetadataKey = "unsupportedDetail";

    /// <summary>
    /// Classifies the subtype/feature-type metadata on a source resource document.
    /// </summary>
    /// <param name="resourceElement">The source layer/table document as returned by the service.</param>
    /// <param name="descriptor">The registry descriptor for <c>resource.subtypes</c>.</param>
    /// <returns>The finding to record against the resource.</returns>
    public static EsriSubtypeFidelityFinding Classify(
        JsonElement resourceElement,
        EsriConstructCapabilityDescriptor descriptor)
        => Classify(EsriSubtypeParser.Parse(resourceElement), descriptor);

    /// <summary>
    /// Classifies an already-computed parse result.
    /// </summary>
    /// <param name="result">The outcome of <see cref="EsriSubtypeParser"/> for the resource.</param>
    /// <param name="descriptor">The registry descriptor for <c>resource.subtypes</c>.</param>
    /// <returns>The finding to record against the resource.</returns>
    public static EsriSubtypeFidelityFinding Classify(
        EsriSubtypeParseResult result,
        EsriConstructCapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (result.UnsupportedCode is not { Length: > 0 } code)
        {
            // Captured: the registry stays the authority for the supported tier.
            return new EsriSubtypeFidelityFinding
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

        return new EsriSubtypeFidelityFinding
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
            [ImportCompatibilityCodes.ArcGisFeatureTypeTemplatesUnsupported] = (
                "A source feature type declares more than one editing template. The canonical model carries a "
                + "single prototype per type, so no feature type on this resource was captured and the target "
                + "layer carries no type-driven editing defaults.",
                ["Choose the editing template each feature type should publish with, or model the additional "
                 + "templates as separate feature types, before cutover."]),
            [ImportCompatibilityCodes.ArcGisFeatureTypeDomainClearingUnsupported] = (
                "A source feature type explicitly clears a field domain. The canonical model expresses only "
                + "domain inheritance or replacement, so no feature type on this resource was captured and the "
                + "target layer carries no per-type domain overrides.",
                ["Replace the cleared domain with an explicit per-type domain, or accept the published field "
                 + "domain for that type, before cutover."]),
            [ImportCompatibilityCodes.ArcGisFeatureTypeIdentityUnsupported] = (
                "A source feature type carries no scalar identifier or no name, so it cannot be projected onto a "
                + "canonical type entry and no feature type on this resource was captured.",
                ["Give every source feature type a scalar id and a name, or drop the unusable type, before cutover."])
        }.ToFrozenDictionary(StringComparer.Ordinal);
}

/// <summary>
/// The subtype fidelity finding recorded against one source resource.
/// </summary>
public sealed record EsriSubtypeFidelityFinding
{
    /// <summary>Automation status; one of <see cref="MigrationFidelityAutomationStatuses"/>.</summary>
    public required string AutomationStatus { get; init; }

    /// <summary>Stable <see cref="ImportCompatibilityCodes"/> value for the finding.</summary>
    public required string Code { get; init; }

    /// <summary>Operator-facing explanation of what was and was not captured.</summary>
    public required string Reason { get; init; }

    /// <summary>Operator follow-up steps.</summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = [];

    /// <summary>Additional finding metadata, empty when the construct was captured.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
