// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Request to discover services and layers from an ArcGIS Server URL.
/// </summary>
public sealed record GeoservicesDiscoveryRequest
{
    /// <summary>
    /// The base URL of the ArcGIS Server service.
    /// Examples:
    /// - https://services.arcgis.com/xxx/arcgis/rest/services/MyService/FeatureServer
    /// - https://server.example.com/arcgis/rest/services/MyFolder/MyService/MapServer
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Optional timeout for the discovery request in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Optional ArcGIS authentication descriptor for discovery requests.
    /// </summary>
    public GeoservicesCredentialDescriptor? Credentials { get; init; }
}

/// <summary>
/// Stable authentication mode labels for ArcGIS GeoServices import requests and inventory artifacts.
/// </summary>
public static class GeoservicesAuthenticationModes
{
    /// <summary>Anonymous access with no supplied credentials.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>ArcGIS token authentication.</summary>
    public const string Token = "token";

    /// <summary>HTTP Basic authentication.</summary>
    public const string Basic = "basic";

    /// <summary>OAuth access-token authentication.</summary>
    public const string OAuth = "oauth";

    /// <summary>The source rejected the supplied identity or forbids access.</summary>
    public const string Denied = "denied";

    /// <summary>The supplied token was rejected as invalid or expired.</summary>
    public const string ExpiredToken = "expired-token";

    /// <summary>The source requires authentication before scanning can proceed.</summary>
    public const string AuthRequired = "auth-required";

    /// <summary>The authentication posture could not be determined.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Secret-safe ArcGIS GeoServices credential descriptor.
/// </summary>
public sealed record GeoservicesCredentialDescriptor
{
    /// <summary>
    /// Authentication mode label. Known values are defined in <see cref="GeoservicesAuthenticationModes"/>.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Optional secret reference that resolves to an ArcGIS token or OAuth access token at execution time.
    /// </summary>
    public string? AccessTokenSecretReference { get; init; }

    /// <summary>
    /// Resolved ArcGIS token or OAuth access token. Queued imports reject this field before persistence.
    /// </summary>
    public string? AccessToken { get; init; }

    /// <summary>
    /// Username for HTTP Basic authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional secret reference that resolves to the HTTP Basic password at execution time.
    /// </summary>
    public string? PasswordSecretReference { get; init; }

    /// <summary>
    /// Resolved HTTP Basic password. Queued imports reject this field before persistence.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional OAuth client identifier associated with the token reference.
    /// </summary>
    public string? OAuthClientId { get; init; }

    /// <summary>
    /// Optional OAuth token endpoint used by operators to identify the token issuer.
    /// </summary>
    public string? OAuthTokenEndpoint { get; init; }

    /// <summary>
    /// Gets whether any credential descriptor or resolved credential value is present.
    /// </summary>
    [JsonIgnore]
    public bool HasCredentialMaterial =>
        !string.IsNullOrWhiteSpace(AccessToken) ||
        !string.IsNullOrWhiteSpace(AccessTokenSecretReference) ||
        !string.IsNullOrWhiteSpace(Username) ||
        !string.IsNullOrWhiteSpace(Password) ||
        !string.IsNullOrWhiteSpace(PasswordSecretReference) ||
        !string.IsNullOrWhiteSpace(OAuthClientId);

    /// <summary>
    /// Gets whether this descriptor carries plaintext secret values in memory.
    /// </summary>
    [JsonIgnore]
    public bool HasPlaintextSecret =>
        !string.IsNullOrWhiteSpace(AccessToken) ||
        !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// Gets the normalized authentication mode implied by the descriptor.
    /// </summary>
    /// <returns>A stable authentication posture label.</returns>
    public string GetNormalizedMode()
    {
        if (!string.IsNullOrWhiteSpace(Mode))
        {
            var normalizedMode = NormalizeMode(Mode);
            if (normalizedMode != GeoservicesAuthenticationModes.Anonymous ||
                string.Equals(Mode.Trim(), GeoservicesAuthenticationModes.Anonymous, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedMode;
            }
        }

        if (!string.IsNullOrWhiteSpace(OAuthClientId))
        {
            return GeoservicesAuthenticationModes.OAuth;
        }

        if (!string.IsNullOrWhiteSpace(AccessToken) ||
            !string.IsNullOrWhiteSpace(AccessTokenSecretReference))
        {
            return GeoservicesAuthenticationModes.Token;
        }

        if (!string.IsNullOrWhiteSpace(Username) ||
            !string.IsNullOrWhiteSpace(Password) ||
            !string.IsNullOrWhiteSpace(PasswordSecretReference))
        {
            return GeoservicesAuthenticationModes.Basic;
        }

        return GeoservicesAuthenticationModes.Anonymous;
    }

    private static string NormalizeMode(string mode)
        => mode.Trim().ToLowerInvariant() switch
        {
            GeoservicesAuthenticationModes.Token => GeoservicesAuthenticationModes.Token,
            GeoservicesAuthenticationModes.Basic => GeoservicesAuthenticationModes.Basic,
            GeoservicesAuthenticationModes.OAuth => GeoservicesAuthenticationModes.OAuth,
            GeoservicesAuthenticationModes.Denied => GeoservicesAuthenticationModes.Denied,
            GeoservicesAuthenticationModes.ExpiredToken => GeoservicesAuthenticationModes.ExpiredToken,
            GeoservicesAuthenticationModes.AuthRequired => GeoservicesAuthenticationModes.AuthRequired,
            GeoservicesAuthenticationModes.Unknown => GeoservicesAuthenticationModes.Unknown,
            _ => GeoservicesAuthenticationModes.Anonymous
        };
}

/// <summary>
/// Request to import a layer from an ArcGIS Server into PostGIS.
/// </summary>
public sealed record GeoservicesImportRequest
{
    /// <summary>
    /// Optional job identifier for progress tracking.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// The base URL of the ArcGIS Server service.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// The ID of the layer to import.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Target table name in PostgreSQL.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Optional target schema for imported operational data.
    /// When omitted, the configured PostgreSQL operational-data schema is used.
    /// </summary>
    public string? TargetSchema { get; init; }

    /// <summary>
    /// Target coordinate reference system ID (for transformation).
    /// Default is 4326 (WGS84).
    /// </summary>
    public int TargetSrid { get; init; } = 4326;

    /// <summary>
    /// Whether to overwrite an existing table.
    /// </summary>
    public bool OverwriteExisting { get; init; }

    /// <summary>
    /// Optional WHERE clause to filter features during import.
    /// Uses Esri SQL syntax.
    /// </summary>
    public string? WhereClause { get; init; }

    /// <summary>
    /// Optional output fields to import (null imports all fields).
    /// </summary>
    public string[]? OutputFields { get; init; }

    /// <summary>
    /// Batch size for paginated feature retrieval.
    /// Default is determined by service's maxRecordCount.
    /// </summary>
    public int? BatchSize { get; init; }

    /// <summary>
    /// Request timeout in seconds for each batch request.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Whether to automatically publish the imported layer.
    /// </summary>
    public bool AutoPublish { get; init; } = true;

    /// <summary>
    /// Optional target Honua service name for auto-publishing.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Optional ArcGIS authentication descriptor. Queued imports must use secret references,
    /// not plaintext token or password values.
    /// </summary>
    public GeoservicesCredentialDescriptor? Credentials { get; init; }

    /// <summary>
    /// Whether to copy feature attachments advertised by the source layer
    /// (<c>hasAttachments == true</c>) into the Honua attachment store after
    /// features have been inserted and the layer auto-published. Skipped when
    /// auto-publish is disabled, no published layer was registered, or no
    /// attachment store is configured.
    /// </summary>
    public bool ImportAttachments { get; init; } = true;
}

/// <summary>
/// Result of a Geoservices import operation.
/// </summary>
public sealed record GeoservicesImportResult
{
    /// <summary>
    /// Whether the import was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Number of features imported.
    /// </summary>
    public int FeatureCount { get; init; }

    /// <summary>
    /// Number of features that failed to import.
    /// </summary>
    public int FailedFeatures { get; init; }

    /// <summary>
    /// Table name created/updated.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// The ID of the published layer (if auto-publish was enabled).
    /// </summary>
    public int? PublishedLayerId { get; init; }

    /// <summary>
    /// The service URL that was imported from.
    /// </summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>
    /// Stable source kind for admin clients that render mixed import job results.
    /// </summary>
    public string SourceKind { get; init; } = "arcgis-geoservices-rest";

    /// <summary>
    /// Source URL alias used by generic admin import result views.
    /// </summary>
    public string SourceUrl => SourceServiceUrl;

    /// <summary>
    /// The layer ID that was imported.
    /// </summary>
    public int SourceLayerId { get; init; }

    /// <summary>
    /// Source layer display name when discovered.
    /// </summary>
    public string? SourceLayerName { get; init; }

    /// <summary>
    /// Error message if import failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Target Honua service name when the import requested or completed publishing.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Published layer ID alias used by generic admin import result views.
    /// </summary>
    public int? LayerId => PublishedLayerId;

    /// <summary>
    /// Import duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Warnings encountered during import.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Number of attachments successfully copied into the Honua attachment store.
    /// Zero when the source layer does not advertise attachments, the attachment
    /// copy step was disabled, or no attachment store was registered.
    /// </summary>
    public int AttachmentCount { get; init; }

    /// <summary>
    /// Number of source attachments that could not be copied. Includes per-attachment
    /// download/upload errors and skipped attachments whose parent ObjectId could not
    /// be mapped to an inserted Honua feature.
    /// </summary>
    public int FailedAttachments { get; init; }

    /// <summary>
    /// True when the import published data but a hard post-publish reconciliation finding routed
    /// the run to operator review instead of completion (parity gate, issue #1380). When set,
    /// <see cref="Success"/> is <c>false</c> even though data was published, and
    /// <see cref="ReconciliationArtifact"/> / <see cref="CatalogReconciliationReport"/> carry the
    /// blocking findings.
    /// </summary>
    public bool NeedsReview { get; init; }

    /// <summary>
    /// Per-layer data-reconciliation artifact produced by the Validating phase, when reconciliation
    /// ran. <c>null</c> when the import did not publish a queryable layer to reconcile against.
    /// </summary>
    public MigrationReconciliationArtifact? ReconciliationArtifact { get; init; }

    /// <summary>
    /// Catalog parity report produced by the Validating phase, when a published Metadata v2 entry
    /// was available to reconcile against. <c>null</c> otherwise.
    /// </summary>
    public MigrationCatalogReconciliationReport? CatalogReconciliationReport { get; init; }

    /// <summary>
    /// Create successful import result.
    /// </summary>
    public static GeoservicesImportResult CreateSuccess(
        string tableName,
        string sourceServiceUrl,
        int sourceLayerId,
        int featureCount,
        int failedFeatures = 0,
        int? publishedLayerId = null,
        string? serviceName = null,
        string? sourceLayerName = null,
        TimeSpan duration = default,
        IReadOnlyList<string>? warnings = null,
        int attachmentCount = 0,
        int failedAttachments = 0,
        MigrationReconciliationArtifact? reconciliationArtifact = null,
        MigrationCatalogReconciliationReport? catalogReconciliationReport = null) =>
        new()
        {
            Success = true,
            TableName = tableName,
            SourceServiceUrl = sourceServiceUrl,
            SourceLayerId = sourceLayerId,
            SourceLayerName = sourceLayerName,
            FeatureCount = featureCount,
            FailedFeatures = failedFeatures,
            PublishedLayerId = publishedLayerId,
            ServiceName = serviceName,
            Duration = duration,
            Warnings = warnings ?? [],
            AttachmentCount = attachmentCount,
            FailedAttachments = failedAttachments,
            ReconciliationArtifact = reconciliationArtifact,
            CatalogReconciliationReport = catalogReconciliationReport
        };

    /// <summary>
    /// Create a result for an import that published data but was routed to operator review by the
    /// post-publish parity gate (issue #1380). Data and the published layer are left in place.
    /// </summary>
    public static GeoservicesImportResult CreateNeedsReview(
        string tableName,
        string sourceServiceUrl,
        int sourceLayerId,
        int featureCount,
        int failedFeatures,
        int? publishedLayerId,
        string? serviceName,
        string? sourceLayerName,
        TimeSpan duration,
        IReadOnlyList<string>? warnings,
        int attachmentCount,
        int failedAttachments,
        MigrationReconciliationArtifact? reconciliationArtifact,
        MigrationCatalogReconciliationReport? catalogReconciliationReport,
        string reviewReason) =>
        new()
        {
            Success = false,
            NeedsReview = true,
            TableName = tableName,
            SourceServiceUrl = sourceServiceUrl,
            SourceLayerId = sourceLayerId,
            SourceLayerName = sourceLayerName,
            FeatureCount = featureCount,
            FailedFeatures = failedFeatures,
            PublishedLayerId = publishedLayerId,
            ServiceName = serviceName,
            Duration = duration,
            Warnings = warnings ?? [],
            AttachmentCount = attachmentCount,
            FailedAttachments = failedAttachments,
            ReconciliationArtifact = reconciliationArtifact,
            CatalogReconciliationReport = catalogReconciliationReport,
            ErrorMessage = reviewReason
        };

    /// <summary>
    /// Create failed import result.
    /// </summary>
    public static GeoservicesImportResult CreateFailure(
        string tableName,
        string sourceServiceUrl,
        int sourceLayerId,
        string errorMessage,
        TimeSpan duration = default) =>
        new()
        {
            Success = false,
            TableName = tableName,
            SourceServiceUrl = sourceServiceUrl,
            SourceLayerId = sourceLayerId,
            ErrorMessage = errorMessage,
            Duration = duration
        };
}
