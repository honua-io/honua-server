// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataCompatibilityCode
{
    public const string StateUnavailable = "metadata.compat.state.unavailable";

    public const string ResourceMissing = "metadata.compat.resource.missing";

    public const string ResourceTypeMismatch = "metadata.compat.resource.type_mismatch";

    public const string FieldMissing = "metadata.compat.field.missing";

    public const string FieldTypeMismatch = "metadata.compat.field.type_mismatch";

    public const string FieldNullabilityMismatch = "metadata.compat.field.nullability_mismatch";

    public const string FieldDomainMismatch = "metadata.compat.field.domain_mismatch";

    public const string FieldIndexMissing = "metadata.compat.field.index_missing";

    public const string IdentifierMissing = "metadata.compat.identifier.missing";

    public const string SpatialGeometryTypeMismatch = "metadata.compat.spatial.geometry_type_mismatch";

    public const string SpatialCrsMismatch = "metadata.compat.spatial.crs_mismatch";

    public const string TemporalFieldMismatch = "metadata.compat.temporal.field_mismatch";

    public const string StorageBindingMissing = "metadata.compat.storage.binding_missing";

    public const string StorageTypeMismatch = "metadata.compat.storage.type_mismatch";

    public const string StorageCapabilityMissing = "metadata.compat.storage.capability_missing";

    public const string StorageLocatorChanged = "metadata.compat.storage.locator_changed";

    public const string ServiceMissing = "metadata.compat.service.missing";

    public const string ServiceTypeMismatch = "metadata.compat.service.type_mismatch";

    public const string ServiceRouteChanged = "metadata.compat.service.route_changed";

    public const string ServiceProtocolMissing = "metadata.compat.service.protocol_missing";

    public const string PublicationMissing = "metadata.compat.publication.missing";

    public const string PublicationTypeMismatch = "metadata.compat.publication.type_mismatch";

    public const string PublicationRouteChanged = "metadata.compat.publication.route_changed";

    public const string PublicationFormatMissing = "metadata.compat.publication.format_missing";

    public const string PublicationCapabilityMissing = "metadata.compat.publication.capability_missing";

    public const string ProjectionRequiredSemanticRemoved = "metadata.compat.projection.required_semantic_removed";

    public const string PolicyReferenceMissing = "metadata.compat.policy.reference_missing";

    public const string ScriptBeforeContractMismatch = "metadata.compat.script.before_contract_mismatch";
}
