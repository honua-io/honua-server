// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Capabilities.Models;

internal static class CapabilityManifestConstants
{
    public const string SchemaVersion = "honua.capability_manifest.v1";
    public const string JsonContentType = "application/json";
    public const string ManifestContentType = "application/vnd.honua.capability-manifest+json";
    public const string NoStoreCacheControl = "no-store";
}

internal sealed record CapabilityManifestDocument
{
    public string SchemaVersion { get; init; } = CapabilityManifestConstants.SchemaVersion;

    public required DateTimeOffset IssuedAt { get; init; }

    public required CapabilityManifestScope Scope { get; init; }

    public required CapabilityManifestServerInfo Server { get; init; }

    public required CapabilityManifestEnvironment Environment { get; init; }

    public required CapabilityManifestPackages Packages { get; init; }

    public required CapabilityManifestCapability[] Capabilities { get; init; }

    public required CapabilityManifestTransports Transports { get; init; }

    public required CapabilityManifestLimits Limits { get; init; }

    public required CapabilityManifestPolicies Policies { get; init; }

    public required CapabilityManifestDeploymentProfile DeploymentProfile { get; init; }

    public CapabilityManifestDataStores? DataStores { get; init; }

    public required CapabilityManifestLink[] Links { get; init; }
}

internal sealed record CapabilityManifestDeploymentProfile
{
    public required bool Configured { get; init; }

    public required string? SchemaVersion { get; init; }

    public required string[] EnabledCapabilities { get; init; }
}

internal sealed record CapabilityManifestScope
{
    public string? TenantId { get; init; }

    // Null, and omitted from the response, when tenant context resolution is off -
    // which is every deployment a licensee may run. A field that is always null
    // invites the question of how to populate it.
    public string? TenantSource { get; init; }

    public string? Environment { get; init; }

    public string? WorkspaceId { get; init; }

    public required bool WorkspaceAvailable { get; init; }

    public string? WorkspaceReasonCode { get; init; }

    public required bool Authenticated { get; init; }
}

internal sealed record CapabilityManifestServerInfo
{
    public required string ServerVersion { get; init; }

    public required string ApiVersion { get; init; }

    public required string MetadataApiVersion { get; init; }

    public required string MetadataSchemaVersion { get; init; }

    public required string DeploymentEnvironment { get; init; }

    // Consumers that bind evidence to a deployment fail closed when the revision is absent rather than substituting the mutable server version.
    public string? DeploymentRevision { get; init; }

    public string? DeploymentRevisionSource { get; init; }

    // ServerRevision duplicates DeploymentRevision because existing evidence and realtime clients read that name.
    public string? ServerRevision { get; init; }
}

internal sealed record CapabilityManifestEnvironment
{
    public string? EnvironmentId { get; init; }

    public required bool Requested { get; init; }

    public required bool Available { get; init; }

    public string? ReasonCode { get; init; }

    public long? Revision { get; init; }

    public DateTimeOffset? LoadedAt { get; init; }
}

internal sealed record CapabilityManifestPackages
{
    public required string[] SchemaVersions { get; init; }

    public required CapabilityManifestPackageFamily[] Families { get; init; }

    public required string[] StorageFamilies { get; init; }

    public required string[] PublicationFamilies { get; init; }
}

internal sealed record CapabilityManifestPackageFamily
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string SchemaVersion { get; init; }

    public required bool Supported { get; init; }
}

internal sealed record CapabilityManifestCapability
{
    public required string Id { get; init; }

    public required string Category { get; init; }

    public required string Lifecycle { get; init; }

    public required bool OptInRequired { get; init; }

    public required bool Supported { get; init; }

    public required bool Available { get; init; }

    public string? ReasonCode { get; init; }

    public string? EntitlementKey { get; init; }

    public string[]? EntitlementKeys { get; init; }

    public string? MinimumEdition { get; init; }

    public string? MessageKey { get; init; }

    public CapabilityManifestRollbackTarget[]? RollbackTargets { get; init; }

    public string? Runner { get; init; }

    public string[]? Schemes { get; init; }
}

internal sealed record CapabilityManifestRollbackTarget
{
    public required string TargetId { get; init; }

    public required string Backend { get; init; }

    public required string State { get; init; }

    public required bool Executable { get; init; }

    public required string ReasonCode { get; init; }
}

internal sealed record CapabilityManifestTransports
{
    public required CapabilityManifestTransportState[] Items { get; init; }

    public required string MtlsMode { get; init; }

    public required bool ForwardedClientCertificateEnabled { get; init; }
}

internal sealed record CapabilityManifestTransportState
{
    public required string Id { get; init; }

    public required bool Supported { get; init; }

    public required bool Available { get; init; }

    public string? ReasonCode { get; init; }

    public string? MessageKey { get; init; }

    public string? ContractVersion { get; init; }
}

internal sealed record CapabilityManifestLimits
{
    public required CapabilityManifestPreviewLimits Preview { get; init; }

    public required CapabilityManifestQueryLimits Query { get; init; }

    public required CapabilityManifestAnalysisLimits Analysis { get; init; }

    public required CapabilityManifestPublicationLimits Publication { get; init; }

    public required CapabilityManifestJobLimits Job { get; init; }

    public required CapabilityManifestUploadLimits Upload { get; init; }

    public required CapabilityManifestStreamingLimits Streaming { get; init; }

    public required CapabilityManifestEditLimits Edit { get; init; }

    public required CapabilityManifestGeometryLimits Geometry { get; init; }

    public required CapabilityManifestAttachmentLimits Attachment { get; init; }
}

internal sealed record CapabilityManifestPreviewLimits
{
    public required long MaxPreviewSizeBytes { get; init; }

    public required int MaxPreviewFeatures { get; init; }

    public required int MaxPreviewCountScan { get; init; }
}

internal sealed record CapabilityManifestQueryLimits
{
    public required int DefaultRecordCount { get; init; }

    public required int MaxRecordCount { get; init; }

    public required int MaxFeatures { get; init; }

    public required int MaxPageSize { get; init; }

    public required int QueryTimeoutSeconds { get; init; }

    public required double MaxBboxAreaSqKm { get; init; }

    public required int MaxFilterDepth { get; init; }

    public required int MaxSpatialOperations { get; init; }
}

internal sealed record CapabilityManifestAnalysisLimits
{
    public required int MaxInputFeatures { get; init; }

    public required int MaxClusters { get; init; }

    public required double MaxDbscanEpsMeters { get; init; }

    public required int MaxKMeansK { get; init; }

    public required double MaxBufferDistanceMeters { get; init; }

    public required double MinDensityCellSizeMeters { get; init; }

    public required double MaxDensityCellSizeMeters { get; init; }

    public required int MaxDensityCells { get; init; }

    public required double MaxDWithinDistanceMeters { get; init; }

    public required int MaxH3CellsPerQuery { get; init; }

    public required int MaxSpatialOperations { get; init; }

    public required int MaxJoins { get; init; }
}

internal sealed record CapabilityManifestPublicationLimits
{
    public required int ConfiguredDeployTargetCount { get; init; }

    public required bool GitOpsManifestExportSupported { get; init; }
}

internal sealed record CapabilityManifestJobLimits
{
    public required int ConfiguredWorkloadCount { get; init; }

    public required int AvailableBackendCount { get; init; }

    public required bool SupportsCancellation { get; init; }

    public required bool SupportsProgressPolling { get; init; }

    public required bool DurableJobRuntimeAvailable { get; init; }

    // The Redis durability attestation never withholds jobs.runner or operations.proposals.
    public CapabilityManifestRedisDurability? RedisDurability { get; init; }
}

internal sealed record CapabilityManifestRedisDurability
{
    public required string Status { get; init; }

    public string? Cause { get; init; }

    public string? Remediation { get; init; }
}

internal sealed record CapabilityManifestUploadLimits
{
    public required long MaxUploadSizeBytes { get; init; }

    public required long MaxFileSizeBytes { get; init; }

    public required int MaxConcurrentUploads { get; init; }

    public required int MaxQueuedUploads { get; init; }

    public required long MaxSecurityScanSizeBytes { get; init; }
}

internal sealed record CapabilityManifestStreamingLimits
{
    public required int MaxConcurrentSessions { get; init; }

    public required int MaxBufferPerConnection { get; init; }

    public required int MaxSubscriptionsPerSession { get; init; }

    public required int MaxSubscriptionIdLength { get; init; }

    public required int MaxControlFrameBytes { get; init; }

    public required int CursorRetentionLimit { get; init; }

    public required double HeartbeatIntervalSeconds { get; init; }

    public required int GrpcStreamBatchSize { get; init; }
}

internal sealed record CapabilityManifestEditLimits
{
    public required int MaxFeaturesPerEdit { get; init; }

    public required int MaxEditsPerTransaction { get; init; }

    public required long MaxPayloadSizeBytes { get; init; }
}

internal sealed record CapabilityManifestGeometryLimits
{
    public required int MaxVerticesPerGeometry { get; init; }

    public required long MaxGeometrySizeBytes { get; init; }

    public required int MaxCoordinatePrecision { get; init; }
}

internal sealed record CapabilityManifestAttachmentLimits
{
    public required int MaxAttachmentsPerFeature { get; init; }

    public required long MaxAttachmentSizeBytes { get; init; }
}

internal sealed record CapabilityManifestPolicies
{
    public required string CurrentEdition { get; init; }

    public required string LicenseValidationState { get; init; }

    public required bool LicenseValid { get; init; }

    public required string[] CallerCapabilities { get; init; }

    public required CapabilityManifestEntitlementDecision[] Entitlements { get; init; }

    public required string AuthorizationNotice { get; init; }
}

internal sealed record CapabilityManifestEntitlementDecision
{
    public required string Key { get; init; }

    public required bool Active { get; init; }

    public required string MinimumEdition { get; init; }

    public string? ReasonCode { get; init; }
}

internal sealed record CapabilityManifestLink
{
    public required string Rel { get; init; }

    public required string Href { get; init; }

    public required string Type { get; init; }
}
