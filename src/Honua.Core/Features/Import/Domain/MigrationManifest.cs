// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Version constants for migration manifest contracts.
/// </summary>
public static class MigrationManifestVersions
{
    /// <summary>
    /// Current GeoServer migration manifest contract version.
    /// </summary>
    public const string V1Alpha1 = "honua.io/migration/v1alpha1";
}

/// <summary>
/// Stable reason codes emitted by migration manifest diagnostics.
/// </summary>
public static class MigrationReasonCodes
{
    /// <summary>
    /// A target secure connection must be created before replay.
    /// </summary>
    public const string CreateSecureConnection = "create-secure-connection";

    /// <summary>
    /// The selected datastore type is not supported by the current translator.
    /// </summary>
    public const string UnsupportedDatastoreType = "unsupported-datastore-type";

    /// <summary>
    /// The selected coverage store is outside the initial translation scope.
    /// </summary>
    public const string UnsupportedCoverageStore = "unsupported-coverage-store";

    /// <summary>
    /// The selected layer references an unsupported source resource.
    /// </summary>
    public const string UnsupportedLayerSource = "unsupported-layer-source";

    /// <summary>
    /// Layer groups require manual recreation in the current migration scope.
    /// </summary>
    public const string UnsupportedLayerGroup = "unsupported-layer-group";

    /// <summary>
    /// The layer SRID could not be derived with enough confidence for direct publish.
    /// </summary>
    public const string ResolveAmbiguousSrid = "resolve-ambiguous-srid";

    /// <summary>
    /// The requested target SRID would require a transform that this ticket does not implement.
    /// </summary>
    public const string UnsupportedTargetSridTransform = "unsupported-target-srid-transform";

    /// <summary>
    /// The layer geometry type could not be resolved from GeoServer discovery metadata.
    /// </summary>
    public const string MissingGeometryType = "missing-geometry-type";

    /// <summary>
    /// The source schema name could not be resolved from GeoServer discovery metadata.
    /// </summary>
    public const string ResolveSourceSchema = "resolve-source-schema";

    /// <summary>
    /// The style uses SLD and requires a manual conversion workflow.
    /// </summary>
    public const string UnsupportedSldStyle = "unsupported-sld-style";

    /// <summary>
    /// The source style format is outside the current translation scope.
    /// </summary>
    public const string UnsupportedStyleFormat = "unsupported-style-format";

    /// <summary>
    /// Manual style conversion is required before the target style can be applied.
    /// </summary>
    public const string ManualStyleConversion = "manual-style-conversion";

    /// <summary>
    /// Inline style content was omitted because it exceeded the manifest size budget.
    /// </summary>
    public const string StyleContentTooLarge = "style-content-too-large";

    /// <summary>
    /// Multiple translated layers mapped to the same target metadata identity.
    /// </summary>
    public const string ConflictingTargetLayerName = "conflicting-target-layer-name";

    /// <summary>
    /// A service-level metadata resource could not be emitted because the translated layer SRIDs conflict.
    /// </summary>
    public const string ConflictingServiceSrid = "conflicting-service-srid";
}

/// <summary>
/// Request for translating GeoServer discovery into a deterministic migration manifest.
/// </summary>
public sealed record GeoServerTranslationRequest
{
    /// <summary>
    /// GeoServer REST base URL.
    /// </summary>
    public required string GeoServerRestUrl { get; init; }

    /// <summary>
    /// Optional GeoServer username.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional GeoServer password.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional workspace filter. When omitted, all workspaces are considered.
    /// </summary>
    public string[]? WorkspaceNames { get; init; }

    /// <summary>
    /// Optional datastore filter using either {@code datastore} or {@code workspace:datastore}.
    /// </summary>
    public string[]? DataStoreNames { get; init; }

    /// <summary>
    /// Optional layer filter using either {@code layer} or {@code workspace:layer}.
    /// </summary>
    public string[]? LayerNames { get; init; }

    /// <summary>
    /// Whether style planning should be included in the manifest.
    /// </summary>
    public bool ImportStyles { get; init; } = true;

    /// <summary>
    /// Whether raw source style content should be included when available.
    /// </summary>
    public bool IncludeStyleContent { get; init; }

    /// <summary>
    /// Optional target SRID requested by the operator.
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Request timeout in seconds for GeoServer calls.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Maximum retry attempts for GeoServer calls.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Optional translation and mapping options reused from the existing GeoServer import slice.
    /// </summary>
    public GeoServerImportOptions? ImportOptions { get; init; }
}

/// <summary>
/// Source type represented by a migration manifest.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationSourceType>))]
public enum MigrationSourceType
{
    /// <summary>
    /// A GeoServer REST source.
    /// </summary>
    GeoServer
}

/// <summary>
/// Status for a translated target plan entry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationPlanStatus>))]
public enum MigrationPlanStatus
{
    /// <summary>
    /// The plan entry is complete enough for direct replay in a future apply workflow.
    /// </summary>
    Ready,

    /// <summary>
    /// The plan entry is usable as a draft but needs operator input before replay.
    /// </summary>
    ManualActionRequired,

    /// <summary>
    /// The source resource cannot be replayed by the current migration workflow.
    /// </summary>
    Unsupported
}

/// <summary>
/// Status for a translated style plan entry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationStyleTranslationStatus>))]
public enum MigrationStyleTranslationStatus
{
    /// <summary>
    /// The source style was translated into a target-native payload.
    /// </summary>
    Translated,

    /// <summary>
    /// The source style requires operator action before it can be applied in Honua.
    /// </summary>
    ManualActionRequired,

    /// <summary>
    /// The source style is outside the current translation scope.
    /// </summary>
    Unsupported
}

/// <summary>
/// Severity level for translation diagnostics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationDiagnosticSeverity>))]
public enum MigrationDiagnosticSeverity
{
    /// <summary>
    /// Informational diagnostic.
    /// </summary>
    Info,

    /// <summary>
    /// Warning diagnostic.
    /// </summary>
    Warning,

    /// <summary>
    /// Error diagnostic.
    /// </summary>
    Error
}

/// <summary>
/// Supported target connection engines for sanitized connection drafts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationConnectionEngine>))]
public enum MigrationConnectionEngine
{
    /// <summary>
    /// PostgreSQL / PostGIS connection draft.
    /// </summary>
    PostgreSql
}

/// <summary>
/// Secret types required to complete a translated connection draft.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationSecretRequirementKind>))]
public enum MigrationSecretRequirementKind
{
    /// <summary>
    /// A database password must be supplied by the operator.
    /// </summary>
    Password
}

/// <summary>
/// Distinct migration manifest returned by translation endpoints.
/// </summary>
public sealed record MigrationManifest
{
    /// <summary>
    /// Migration manifest API version.
    /// </summary>
    public string ApiVersion { get; init; } = MigrationManifestVersions.V1Alpha1;

    /// <summary>
    /// Timestamp when the manifest was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Stable content hash of the manifest excluding non-semantic timestamps.
    /// </summary>
    public string ManifestHash { get; init; } = string.Empty;

    /// <summary>
    /// Version of the translator that generated the manifest.
    /// </summary>
    public string TranslatorVersion { get; init; } = string.Empty;

    /// <summary>
    /// Legacy source type represented by the manifest.
    /// </summary>
    public MigrationSourceType SourceType { get; init; } = MigrationSourceType.GeoServer;

    /// <summary>
    /// Sanitized source provenance for review and replay.
    /// </summary>
    public GeoServerMigrationSourceSummary SourceSummary { get; init; } = new();

    /// <summary>
    /// Selection and mapping inputs that shaped the translation output.
    /// </summary>
    public GeoServerMigrationSelection Selection { get; init; } = new();

    /// <summary>
    /// Aggregate counts describing the translated output.
    /// </summary>
    public MigrationManifestSummary Summary { get; init; } = new();

    /// <summary>
    /// Sanitized downstream secure-connection drafts.
    /// </summary>
    public IReadOnlyList<MigrationConnectionDraft> ConnectionDrafts { get; init; } = [];

    /// <summary>
    /// Planned service/layer publish steps for later replay.
    /// </summary>
    public IReadOnlyList<MigrationPublishPlanEntry> PublishPlan { get; init; } = [];

    /// <summary>
    /// Metadata resources that already fit Honua's declarative metadata contract.
    /// </summary>
    public IReadOnlyList<MetadataResource> MetadataResources { get; init; } = [];

    /// <summary>
    /// Style translation plan entries for later replay or manual conversion.
    /// </summary>
    public IReadOnlyList<MigrationStylePlanEntry> StylePlan { get; init; } = [];

    /// <summary>
    /// Explicit diagnostics for unsupported or manual migration work.
    /// </summary>
    public IReadOnlyList<MigrationDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Non-secret provenance summary for a translated GeoServer source.
/// </summary>
public sealed record GeoServerMigrationSourceSummary
{
    /// <summary>
    /// Source GeoServer REST URL.
    /// </summary>
    public string GeoServerRestUrl { get; init; } = string.Empty;

    /// <summary>
    /// Source GeoServer host name.
    /// </summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// Source GeoServer version when available.
    /// </summary>
    public string? GeoServerVersion { get; init; }

    /// <summary>
    /// Stable fingerprint of the relevant discovered source configuration.
    /// </summary>
    public string SourceFingerprint { get; init; } = string.Empty;

    /// <summary>
    /// Total workspaces discovered from the source.
    /// </summary>
    public int WorkspaceCount { get; init; }

    /// <summary>
    /// Total datastores discovered from the source.
    /// </summary>
    public int DataStoreCount { get; init; }

    /// <summary>
    /// Total coverage stores discovered from the source.
    /// </summary>
    public int CoverageStoreCount { get; init; }

    /// <summary>
    /// Total layers discovered from the source.
    /// </summary>
    public int LayerCount { get; init; }

    /// <summary>
    /// Total layer groups discovered from the source.
    /// </summary>
    public int LayerGroupCount { get; init; }

    /// <summary>
    /// Total styles discovered from the source.
    /// </summary>
    public int StyleCount { get; init; }

    /// <summary>
    /// Compatibility breakdown reported by the source discovery step.
    /// </summary>
    public MigrationSourceCompatibilitySummary Compatibility { get; init; } = new();
}

/// <summary>
/// Compatibility totals captured from GeoServer discovery.
/// </summary>
public sealed record MigrationSourceCompatibilitySummary
{
    /// <summary>
    /// Number of fully compatible resources reported by discovery.
    /// </summary>
    public int FullyCompatibleResources { get; init; }

    /// <summary>
    /// Number of partially compatible resources reported by discovery.
    /// </summary>
    public int PartiallyCompatibleResources { get; init; }

    /// <summary>
    /// Number of incompatible resources reported by discovery.
    /// </summary>
    public int IncompatibleResources { get; init; }

    /// <summary>
    /// Overall compatibility percentage reported by discovery.
    /// </summary>
    public double CompatibilityPercentage { get; init; }
}

/// <summary>
/// Captures the selection and mapping inputs used to generate the manifest.
/// </summary>
public sealed record GeoServerMigrationSelection
{
    /// <summary>
    /// Workspace filters applied during translation.
    /// </summary>
    public IReadOnlyList<string> WorkspaceNames { get; init; } = [];

    /// <summary>
    /// Datastore filters applied during translation.
    /// </summary>
    public IReadOnlyList<string> DataStoreNames { get; init; } = [];

    /// <summary>
    /// Layer filters applied during translation.
    /// </summary>
    public IReadOnlyList<string> LayerNames { get; init; } = [];

    /// <summary>
    /// Whether style planning was included.
    /// </summary>
    public bool ImportStyles { get; init; }

    /// <summary>
    /// Whether raw source style content was requested.
    /// </summary>
    public bool IncludeStyleContent { get; init; }

    /// <summary>
    /// Optional target SRID requested by the operator.
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Workspace-to-service name overrides.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkspaceNameMappings { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback target service name for global or unmapped resources.
    /// </summary>
    public string DefaultWorkspaceName { get; init; } = "geoserver-import";
}

/// <summary>
/// Summary counts for translated manifest sections.
/// </summary>
public sealed record MigrationManifestSummary
{
    /// <summary>
    /// Number of selected workspaces represented in the manifest.
    /// </summary>
    public int SelectedWorkspaceCount { get; init; }

    /// <summary>
    /// Number of selected datastores represented in the manifest.
    /// </summary>
    public int SelectedDataStoreCount { get; init; }

    /// <summary>
    /// Number of selected coverage stores represented in the manifest.
    /// </summary>
    public int SelectedCoverageStoreCount { get; init; }

    /// <summary>
    /// Number of selected layers represented in the manifest.
    /// </summary>
    public int SelectedLayerCount { get; init; }

    /// <summary>
    /// Number of selected layer groups represented in the manifest.
    /// </summary>
    public int SelectedLayerGroupCount { get; init; }

    /// <summary>
    /// Number of style plan entries represented in the manifest.
    /// </summary>
    public int SelectedStyleCount { get; init; }

    /// <summary>
    /// Number of connection drafts emitted.
    /// </summary>
    public int ConnectionDraftCount { get; init; }

    /// <summary>
    /// Number of publish plan entries emitted.
    /// </summary>
    public int PublishPlanCount { get; init; }

    /// <summary>
    /// Number of publish plan entries ready for direct replay.
    /// </summary>
    public int ReadyPublishPlanCount { get; init; }

    /// <summary>
    /// Number of metadata resources emitted.
    /// </summary>
    public int MetadataResourceCount { get; init; }

    /// <summary>
    /// Number of style plan entries emitted.
    /// </summary>
    public int StylePlanCount { get; init; }

    /// <summary>
    /// Number of diagnostics emitted.
    /// </summary>
    public int DiagnosticCount { get; init; }

    /// <summary>
    /// Number of entries that require manual action before replay.
    /// </summary>
    public int ManualActionCount { get; init; }

    /// <summary>
    /// Number of entries currently outside the automatic migration scope.
    /// </summary>
    public int UnsupportedCount { get; init; }
}

/// <summary>
/// Sanitized draft for a later secure-connection creation step.
/// </summary>
public sealed record MigrationConnectionDraft
{
    /// <summary>
    /// Stable alias referenced by downstream publish plan entries.
    /// </summary>
    public string Alias { get; init; } = string.Empty;

    /// <summary>
    /// Target connection engine.
    /// </summary>
    public MigrationConnectionEngine Engine { get; init; } = MigrationConnectionEngine.PostgreSql;

    /// <summary>
    /// Database host name.
    /// </summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// Database port.
    /// </summary>
    public int Port { get; init; } = 5432;

    /// <summary>
    /// Database name.
    /// </summary>
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>
    /// Default schema name when the source store exposed one.
    /// </summary>
    public string? SchemaName { get; init; }

    /// <summary>
    /// Non-secret username hint taken from the source store.
    /// </summary>
    public string? UsernameHint { get; init; }

    /// <summary>
    /// SSL mode hint taken from the source store when present.
    /// </summary>
    public string? SslMode { get; init; }

    /// <summary>
    /// Source workspace for provenance.
    /// </summary>
    public string SourceWorkspace { get; init; } = string.Empty;

    /// <summary>
    /// Source datastore name for provenance.
    /// </summary>
    public string SourceDataStore { get; init; } = string.Empty;

    /// <summary>
    /// Readiness state for later replay.
    /// </summary>
    public MigrationPlanStatus Status { get; init; } = MigrationPlanStatus.ManualActionRequired;

    /// <summary>
    /// Secret material the operator must supply later.
    /// </summary>
    public IReadOnlyList<MigrationSecretRequirement> SecretRequirements { get; init; } = [];
}

/// <summary>
/// Operator-supplied secret material required to complete a translated connection draft.
/// </summary>
public sealed record MigrationSecretRequirement
{
    /// <summary>
    /// Secret material kind.
    /// </summary>
    public MigrationSecretRequirementKind Kind { get; init; }

    /// <summary>
    /// Guidance describing how the missing secret is used downstream.
    /// </summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Planned downstream publish action for a translated vector layer.
/// </summary>
public sealed record MigrationPublishPlanEntry
{
    /// <summary>
    /// Stable source layer key.
    /// </summary>
    public string SourceLayerKey { get; init; } = string.Empty;

    /// <summary>
    /// Source workspace name.
    /// </summary>
    public string SourceWorkspace { get; init; } = string.Empty;

    /// <summary>
    /// Source layer name.
    /// </summary>
    public string SourceLayerName { get; init; } = string.Empty;

    /// <summary>
    /// Source datastore name.
    /// </summary>
    public string SourceDataStore { get; init; } = string.Empty;

    /// <summary>
    /// Source schema name.
    /// </summary>
    public string? SourceSchemaName { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    public string SourceTableName { get; init; } = string.Empty;

    /// <summary>
    /// Source geometry column name when known.
    /// </summary>
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Source geometry type when known.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Source SRID when known.
    /// </summary>
    public int? SourceSrid { get; init; }

    /// <summary>
    /// Requested target SRID when one was supplied.
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Target connection alias referenced by the later secure-connection workflow.
    /// </summary>
    public string ConnectionAlias { get; init; } = string.Empty;

    /// <summary>
    /// Target Honua service name.
    /// </summary>
    public string TargetServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Target Honua layer name.
    /// </summary>
    public string TargetLayerName { get; init; } = string.Empty;

    /// <summary>
    /// Whether the current plan is complete enough for a direct publish step.
    /// </summary>
    public bool EligibleForDirectPublish { get; init; }

    /// <summary>
    /// Readiness state for later replay.
    /// </summary>
    public MigrationPlanStatus Status { get; init; } = MigrationPlanStatus.ManualActionRequired;

    /// <summary>
    /// Reason codes attached to this publish plan entry.
    /// </summary>
    public IReadOnlyList<string> DiagnosticCodes { get; init; } = [];
}

/// <summary>
/// Planned downstream style action for a translated source layer/style pair.
/// </summary>
public sealed record MigrationStylePlanEntry
{
    /// <summary>
    /// Stable source layer key.
    /// </summary>
    public string SourceLayerKey { get; init; } = string.Empty;

    /// <summary>
    /// Source style name.
    /// </summary>
    public string SourceStyleName { get; init; } = string.Empty;

    /// <summary>
    /// Source style workspace when the style is workspace-scoped.
    /// </summary>
    public string? SourceStyleWorkspace { get; init; }

    /// <summary>
    /// Source style format.
    /// </summary>
    public string SourceFormat { get; init; } = string.Empty;

    /// <summary>
    /// Translation readiness state for the style.
    /// </summary>
    public MigrationStyleTranslationStatus TranslationStatus { get; init; } = MigrationStyleTranslationStatus.Unsupported;

    /// <summary>
    /// Target style name when the translator can name a future target artifact.
    /// </summary>
    public string? TargetStyleName { get; init; }

    /// <summary>
    /// Target payload format when a target-native style payload is available.
    /// </summary>
    public string? TargetFormat { get; init; }

    /// <summary>
    /// Target style payload when translation succeeded.
    /// </summary>
    public JsonElement? TargetStyle { get; init; }

    /// <summary>
    /// Source reference URL for operator handoff.
    /// </summary>
    public string? SourceReferenceUrl { get; init; }

    /// <summary>
    /// Raw source style content when it was explicitly requested and kept within the manifest size budget.
    /// </summary>
    public string? SourceContent { get; init; }

    /// <summary>
    /// Reason codes attached to this style entry.
    /// </summary>
    public IReadOnlyList<string> DiagnosticCodes { get; init; } = [];
}

/// <summary>
/// Explicit translation finding describing unsupported or manual migration work.
/// </summary>
public sealed record MigrationDiagnostic
{
    /// <summary>
    /// Diagnostic severity.
    /// </summary>
    public MigrationDiagnosticSeverity Severity { get; init; } = MigrationDiagnosticSeverity.Warning;

    /// <summary>
    /// Stable reason code.
    /// </summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// Source resource type associated with the finding.
    /// </summary>
    public string SourceResourceType { get; init; } = string.Empty;

    /// <summary>
    /// Stable source resource key associated with the finding.
    /// </summary>
    public string SourceKey { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable diagnostic message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Concrete operator follow-up actions.
    /// </summary>
    public IReadOnlyList<string> ManualActions { get; init; } = [];
}
