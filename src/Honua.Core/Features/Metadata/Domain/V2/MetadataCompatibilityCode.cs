// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Stable Metadata v2 compatibility finding codes.
/// </summary>
public static class MetadataCompatibilityCode
{
    /// <summary>Required source, package, or target state is unavailable.</summary>
    public const string StateUnavailable = "metadata.compat.state.unavailable";

    /// <summary>Required semantic artifact is missing.</summary>
    public const string ResourceMissing = "metadata.compat.resource.missing";

    /// <summary>Semantic artifact kind differs between source and target.</summary>
    public const string ResourceTypeMismatch = "metadata.compat.resource.type_mismatch";

    /// <summary>Required field is missing.</summary>
    public const string FieldMissing = "metadata.compat.field.missing";

    /// <summary>Field type differs between source and target.</summary>
    public const string FieldTypeMismatch = "metadata.compat.field.type_mismatch";

    /// <summary>Field nullability differs between source and target.</summary>
    public const string FieldNullabilityMismatch = "metadata.compat.field.nullability_mismatch";

    /// <summary>Required canonical field domain is missing or incompatible.</summary>
    public const string FieldDomainMismatch = "metadata.compat.field.domain_mismatch";

    /// <summary>Required canonical field index is missing or incompatible.</summary>
    public const string FieldIndexMissing = "metadata.compat.field.index_missing";

    /// <summary>Required identifier field is missing.</summary>
    public const string IdentifierMissing = "metadata.compat.identifier.missing";

    /// <summary>Geometry type differs between source and target.</summary>
    public const string SpatialGeometryTypeMismatch = "metadata.compat.spatial.geometry_type_mismatch";

    /// <summary>CRS/SRID differs or is unavailable for comparison.</summary>
    public const string SpatialCrsMismatch = "metadata.compat.spatial.crs_mismatch";

    /// <summary>Temporal field metadata differs between source and target.</summary>
    public const string TemporalFieldMismatch = "metadata.compat.temporal.field_mismatch";

    /// <summary>Required primary storage binding is missing.</summary>
    public const string StorageBindingMissing = "metadata.compat.storage.binding_missing";

    /// <summary>Storage type differs between source and target.</summary>
    public const string StorageTypeMismatch = "metadata.compat.storage.type_mismatch";

    /// <summary>Required storage capability is missing.</summary>
    public const string StorageCapabilityMissing = "metadata.compat.storage.capability_missing";

    /// <summary>Storage locator changed and needs review.</summary>
    public const string StorageLocatorChanged = "metadata.compat.storage.locator_changed";

    /// <summary>Required service is missing.</summary>
    public const string ServiceMissing = "metadata.compat.service.missing";

    /// <summary>Service type differs between source and target.</summary>
    public const string ServiceTypeMismatch = "metadata.compat.service.type_mismatch";

    /// <summary>Service route changed and clients may need a service revision.</summary>
    public const string ServiceRouteChanged = "metadata.compat.service.route_changed";

    /// <summary>Required service protocol is absent from target.</summary>
    public const string ServiceProtocolMissing = "metadata.compat.service.protocol_missing";

    /// <summary>Required publication is missing.</summary>
    public const string PublicationMissing = "metadata.compat.publication.missing";

    /// <summary>Publication type differs between source and target.</summary>
    public const string PublicationTypeMismatch = "metadata.compat.publication.type_mismatch";

    /// <summary>Publication resource, service, route, path, layer index, or local id changed.</summary>
    public const string PublicationRouteChanged = "metadata.compat.publication.route_changed";

    /// <summary>Required publication format is absent from target.</summary>
    public const string PublicationFormatMissing = "metadata.compat.publication.format_missing";

    /// <summary>Required publication capability is absent from target.</summary>
    public const string PublicationCapabilityMissing = "metadata.compat.publication.capability_missing";

    /// <summary>Required projection semantic is absent from target.</summary>
    public const string ProjectionRequiredSemanticRemoved = "metadata.compat.projection.required_semantic_removed";

    /// <summary>Required policy or role reference is absent from target.</summary>
    public const string PolicyReferenceMissing = "metadata.compat.policy.reference_missing";

    /// <summary>A declared data script before-contract does not match target state.</summary>
    public const string ScriptBeforeContractMismatch = "metadata.compat.script.before_contract_mismatch";
}
