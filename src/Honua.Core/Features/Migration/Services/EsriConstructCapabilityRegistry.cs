// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Frozen-dictionary registry of <see cref="EsriConstructCapabilityDescriptor"/>
/// entries seeded with the ArcGIS constructs recognised by the import fidelity
/// classifier. Unknown keys resolve to a safe <c>manual-review</c> fallback.
/// </summary>
public sealed class EsriConstructCapabilityRegistry : IEsriConstructCapabilityRegistry
{
    /// <summary>
    /// Construct keys for ArcGIS service-level fidelity records.
    /// </summary>
    public static class Keys
    {
        /// <summary>Service identity and source URL.</summary>
        public const string ServiceIdentity = "service.identity";

        /// <summary>Service-level capabilities advertised by the ArcGIS service root.</summary>
        public const string ServiceCapabilities = "service.capabilities";

        /// <summary>Layer or table identity.</summary>
        public const string ResourceIdentity = "resource.identity";

        /// <summary>Resource-level capabilities including query access.</summary>
        public const string ResourceCapabilities = "resource.capabilities";

        /// <summary>Resource field metadata.</summary>
        public const string ResourceFields = "resource.fields";

        /// <summary>Resource field domains (coded-value and range).</summary>
        public const string ResourceDomains = "resource.domains";

        /// <summary>Resource subtype metadata.</summary>
        public const string ResourceSubtypes = "resource.subtypes";

        /// <summary>Resource relationship metadata.</summary>
        public const string ResourceRelationships = "resource.relationships";

        /// <summary>Resource attachment capability.</summary>
        public const string ResourceAttachments = "resource.attachments";

        /// <summary>
        /// Resource renderer / symbology. The per-style tier remains driven by
        /// <c>MigrationInventoryStyle.Compatibility</c>; this entry advertises
        /// the construct's capability flags and the unknown fallback.
        /// </summary>
        public const string ResourceRenderers = "resource.renderers";

        /// <summary>Resource temporal/time metadata.</summary>
        public const string ResourceTimeMetadata = "resource.time-metadata";

        // --- Portal-facade serve lane (#1382) -------------------------------
        // The Portal facade re-exposes imported Esri-shaped Metadata v2 services
        // as ArcGIS Portal items. These keys carry the per-service-construct
        // "can this construct be served through the facade?" verdict so the
        // facade lane resolves from the shared registry instead of a private
        // protocol→item-type table.

        /// <summary>Feature service exposed through the Portal facade as a feature-service item.</summary>
        public const string FacadeFeatureService = "facade.feature-service";

        /// <summary>Map service exposed through the Portal facade as a map-service item.</summary>
        public const string FacadeMapService = "facade.map-service";

        /// <summary>Image service exposed through the Portal facade as an image-service item.</summary>
        public const string FacadeImageService = "facade.image-service";

        // --- GP-translation lane (#1382) ------------------------------------
        // The geoprocessing migration evidence classifier groups built-in
        // processes into construct families. These keys carry the per-family
        // automation verdict so the GP lane resolves from the shared registry
        // instead of a private process-id/category table.

        /// <summary>First-slice deterministic vector geoprocessing family.</summary>
        public const string GpVectorDeterministic = "gp.vector-deterministic";

        /// <summary>Destructive data-management geoprocessing family (approval-gated).</summary>
        public const string GpDestructiveDataManagement = "gp.destructive-data-management";

        /// <summary>Heavyweight raster/surface geoprocessing family.</summary>
        public const string GpRasterSurface = "gp.raster-surface";

        /// <summary>Heavyweight raster-producing conversion geoprocessing family.</summary>
        public const string GpRasterConversion = "gp.raster-conversion";

        /// <summary>Non-destructive data-management geoprocessing family.</summary>
        public const string GpDataManagement = "gp.data-management";

        /// <summary>Geoprocessing family not included in the first migration evidence slice.</summary>
        public const string GpUnsupported = "gp.unsupported";
    }

    /// <summary>
    /// Descriptor returned by <see cref="ResolveOrUnknown(string)"/> when a key is not registered.
    /// </summary>
    public static EsriConstructCapabilityDescriptor UnknownDescriptor { get; } = new()
    {
        ConstructKey = "unknown",
        AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
        Code = ImportCompatibilityCodes.ManualReview,
        Reason = "Construct category is not recognised by the import fidelity registry.",
        CanTransform = false,
        CanServe = false,
        RequiresCheck = true,
        Notes = "Fallback descriptor for unregistered construct keys."
    };

    /// <summary>
    /// Built-in descriptor set seeded into the registry. Mirrors the
    /// pre-registry classification strings emitted by <c>GeoservicesImportService.Fidelity.cs</c>.
    /// </summary>
    public static IReadOnlyList<EsriConstructCapabilityDescriptor> BuiltInDescriptors { get; } =
    [
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ServiceIdentity,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "Service identity and source URL were captured for deterministic migration planning.",
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ServiceCapabilities,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "Service capabilities were captured from the ArcGIS service root.",
            UnsupportedAutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            UnsupportedCode = ImportCompatibilityCodes.ManualReview,
            UnsupportedReason = "Service capabilities were not advertised and require operator confirmation.",
            UnsupportedManualSteps = ["Confirm source service capabilities before migration."],
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceIdentity,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "Layer or table identity was captured with a stable source identifier.",
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceCapabilities,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "Queryable resource capabilities were captured for import planning.",
            UnsupportedAutomationStatus = MigrationFidelityAutomationStatuses.Unsupported,
            UnsupportedCode = ImportCompatibilityCodes.ArcGisQueryCapabilityMissing,
            UnsupportedReason = "The resource does not advertise query capability.",
            UnsupportedManualSteps = ["Enable query access or export the source data through another path before migration."],
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceFields,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "Field names, aliases, source types, nullability, and compact domain metadata were captured.",
            UnsupportedAutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            UnsupportedCode = ImportCompatibilityCodes.ManualReview,
            UnsupportedReason = "No field metadata was advertised for this resource.",
            UnsupportedManualSteps = ["Confirm source fields before import."],
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceDomains,
            AutomationStatus = MigrationFidelityAutomationStatuses.Assisted,
            Code = ImportCompatibilityCodes.ManualReview,
            Reason = "Domain metadata was captured for review; target domain enforcement is not automated by this import slice.",
            ManualSteps = ["Review coded-value and range domains before publishing target field configuration."],
            CanTransform = false,
            CanServe = false,
            RequiresCheck = true
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceSubtypes,
            AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            Code = ImportCompatibilityCodes.ArcGisSubtypesManualReview,
            Reason = "Subtype metadata was detected and captured for operator review; automated subtype migration is not implemented.",
            ManualSteps = ["Recreate subtype behavior or document an accepted gap before cutover."],
            CanTransform = false,
            CanServe = false,
            RequiresCheck = true
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceRelationships,
            AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            Code = ImportCompatibilityCodes.ArcGisRelationshipsManualReview,
            Reason = "Relationship metadata was detected and captured for operator review; automated relationship migration is not implemented.",
            ManualSteps = ["Map related layers or tables to target relationship configuration before cutover."],
            CanTransform = false,
            CanServe = false,
            RequiresCheck = true
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceAttachments,
            AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            Code = ImportCompatibilityCodes.ArcGisAttachments,
            Reason = "Attachment capability was detected, but attachment content migration is not implemented by this slice.",
            ManualSteps = ["Plan a separate attachment migration alongside the core data import."],
            CanTransform = false,
            CanServe = false,
            RequiresCheck = true
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceRenderers,
            AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            Code = ImportCompatibilityCodes.ManualReview,
            Reason = "Renderer fidelity defaults to manual review when no style compatibility verdict is supplied.",
            CanTransform = false,
            CanServe = false,
            RequiresCheck = true,
            Notes = "Per-style renderer tier is supplied by MigrationInventoryStyle.Compatibility; this entry advertises capability flags and the unknown fallback."
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.ResourceTimeMetadata,
            AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            Code = ImportCompatibilityCodes.ArcGisTimeMetadataManualReview,
            Reason = "Time metadata was detected and captured for operator review; automated temporal service configuration is not implemented.",
            ManualSteps = ["Configure or waive target temporal metadata before cutover."],
            CanTransform = false,
            CanServe = false,
            RequiresCheck = true
        },

        // --- Portal-facade serve lane (#1382) -------------------------------
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.FacadeFeatureService,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "FeatureServer services are re-exposed through the Portal facade as feature-service items.",
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.FacadeMapService,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "MapServer services are re-exposed through the Portal facade as map-service items.",
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.FacadeImageService,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "ImageServer services are re-exposed through the Portal facade as image-service items.",
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },

        // --- GP-translation lane (#1382) ------------------------------------
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.GpVectorDeterministic,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "First-slice deterministic vector process evidence.",
            CanTransform = true,
            CanServe = true,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.GpDestructiveDataManagement,
            AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            Code = ImportCompatibilityCodes.ManualReview,
            Reason = "Destructive data-management process; execution requires operator approval.",
            ManualSteps = ["Obtain operator approval before executing this destructive process."],
            CanTransform = false,
            CanServe = false,
            RequiresCheck = true
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.GpRasterSurface,
            AutomationStatus = MigrationFidelityAutomationStatuses.Assisted,
            Code = ImportCompatibilityCodes.ManualReview,
            Reason = "Heavyweight raster/surface family; native-profile execution routed to the GDAL worker image, validation-only in the lean image.",
            CanTransform = false,
            CanServe = false,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.GpRasterConversion,
            AutomationStatus = MigrationFidelityAutomationStatuses.Assisted,
            Code = ImportCompatibilityCodes.ManualReview,
            Reason = "Heavyweight raster conversion family; catalog and validation only in this evidence slice.",
            CanTransform = false,
            CanServe = false,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.GpDataManagement,
            AutomationStatus = MigrationFidelityAutomationStatuses.Assisted,
            Code = ImportCompatibilityCodes.ManualReview,
            Reason = "Data-management process is inventoried for migration review, not projected as automated runtime evidence.",
            CanTransform = false,
            CanServe = false,
            RequiresCheck = false
        },
        new EsriConstructCapabilityDescriptor
        {
            ConstructKey = Keys.GpUnsupported,
            AutomationStatus = MigrationFidelityAutomationStatuses.Unsupported,
            Code = ImportCompatibilityCodes.ManualReview,
            Reason = "Not included in the first process migration evidence slice.",
            CanTransform = false,
            CanServe = false,
            RequiresCheck = false
        }
    ];

    private readonly FrozenDictionary<string, EsriConstructCapabilityDescriptor> _byConstructKey;

    /// <summary>
    /// Builds a registry from the supplied descriptors. Duplicate construct
    /// keys throw <see cref="InvalidOperationException"/> at construction time.
    /// </summary>
    public EsriConstructCapabilityRegistry(IEnumerable<EsriConstructCapabilityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var dict = new Dictionary<string, EsriConstructCapabilityDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (string.IsNullOrWhiteSpace(descriptor.ConstructKey))
            {
                throw new InvalidOperationException(
                    "Esri construct capability descriptor must have a non-empty ConstructKey.");
            }

            if (!dict.TryAdd(descriptor.ConstructKey, descriptor))
            {
                throw new InvalidOperationException(
                    $"Duplicate Esri construct capability descriptor registered for key '{descriptor.ConstructKey}'.");
            }
        }

        _byConstructKey = dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public EsriConstructCapabilityDescriptor ResolveOrUnknown(string constructKey)
    {
        if (!string.IsNullOrEmpty(constructKey) && _byConstructKey.TryGetValue(constructKey, out var descriptor))
        {
            return descriptor;
        }

        return UnknownDescriptor;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> RegisteredConstructKeys => _byConstructKey.Keys;
}
