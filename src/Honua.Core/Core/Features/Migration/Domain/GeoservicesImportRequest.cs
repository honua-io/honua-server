// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

public sealed record GeoservicesDiscoveryRequest
{
    public required string ServiceUrl { get; init; }

    public int TimeoutSeconds { get; init; } = 30;

    public GeoservicesCredentialDescriptor? Credentials { get; init; }
}

public static class GeoservicesAuthenticationModes
{
    public const string Anonymous = "anonymous";

    public const string Token = "token";

    public const string Basic = "basic";

    public const string OAuth = "oauth";

    public const string Denied = "denied";

    public const string ExpiredToken = "expired-token";

    public const string AuthRequired = "auth-required";

    public const string Unknown = "unknown";
}

public sealed record GeoservicesCredentialDescriptor
{
    public string? Mode { get; init; }

    public string? AccessTokenSecretReference { get; init; }

    public string? AccessToken { get; init; }

    public string? Username { get; init; }

    public string? PasswordSecretReference { get; init; }

    public string? Password { get; init; }

    public string? OAuthClientId { get; init; }

    public string? OAuthTokenEndpoint { get; init; }

    [JsonIgnore]
    public bool HasCredentialMaterial =>
        !string.IsNullOrWhiteSpace(AccessToken) ||
        !string.IsNullOrWhiteSpace(AccessTokenSecretReference) ||
        !string.IsNullOrWhiteSpace(Username) ||
        !string.IsNullOrWhiteSpace(Password) ||
        !string.IsNullOrWhiteSpace(PasswordSecretReference) ||
        !string.IsNullOrWhiteSpace(OAuthClientId);

    [JsonIgnore]
    public bool HasPlaintextSecret =>
        !string.IsNullOrWhiteSpace(AccessToken) ||
        !string.IsNullOrWhiteSpace(Password);

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

public sealed record GeoservicesImportRequest
{
    public string? JobId { get; init; }

    public bool DeferRelationshipApplyToBatch { get; init; }


    public required string ServiceUrl { get; init; }

    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public string? TargetSchema { get; init; }

    public int TargetSrid { get; init; } = 4326;

    public bool OverwriteExisting { get; init; }

    public string? WhereClause { get; init; }

    public string[]? OutputFields { get; init; }

    public int? BatchSize { get; init; }

    public int RequestTimeoutSeconds { get; init; } = 120;

    public int MaxRetries { get; init; } = 3;

    public bool AutoPublish { get; init; } = true;

    public string? ServiceName { get; init; }

    public GeoservicesImportTarget? Target { get; init; }

    // Queued imports must carry secret references, not plaintext tokens or passwords.
    public GeoservicesCredentialDescriptor? Credentials { get; init; }

    public bool ImportAttachments { get; init; } = true;
}

public sealed record GeoservicesImportResult
{
    public required bool Success { get; init; }

    public int FeatureCount { get; init; }

    public int FailedFeatures { get; init; }

    public required string TableName { get; init; }

    public int? PublishedLayerId { get; init; }

    public required string SourceServiceUrl { get; init; }

    public string SourceKind { get; init; } = MigrationSourceKinds.GeoServicesRest;

    public string SourceUrl => SourceServiceUrl;

    public int SourceLayerId { get; init; }

    public string? SourceLayerName { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorCode { get; init; }

    public string? ServiceName { get; init; }

    public int? LayerId => PublishedLayerId;

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int AttachmentCount { get; init; }

    public int FailedAttachments { get; init; }

    public bool NeedsReview { get; init; }

    // Rows already committed stay committed: a later cancel or publication failure must not be reported as a rollback.
    public bool DataCommitted { get; init; }

    public MigrationReconciliationArtifact? ReconciliationArtifact { get; init; }

    public MigrationCatalogReconciliationReport? CatalogReconciliationReport { get; init; }

    // A finished job whose checks were skipped is not full fidelity.
    public string FidelityVerdict { get; init; } = MigrationFidelityVerdicts.Unverified;

    public MigrationFidelityDifference[] FidelityDifferences { get; init; } = [];

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
        MigrationCatalogReconciliationReport? catalogReconciliationReport = null,
        string fidelityVerdict = MigrationFidelityVerdicts.Unverified,
        MigrationFidelityDifference[]? fidelityDifferences = null) =>
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
            CatalogReconciliationReport = catalogReconciliationReport,
            FidelityVerdict = fidelityVerdict,
            FidelityDifferences = fidelityDifferences ?? [],
            DataCommitted = true
        };

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
        string reviewReason,
        string fidelityVerdict = MigrationFidelityVerdicts.Incomplete,
        MigrationFidelityDifference[]? fidelityDifferences = null) =>
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
            FidelityVerdict = fidelityVerdict,
            FidelityDifferences = fidelityDifferences ?? [],
            ErrorMessage = reviewReason,
            DataCommitted = true
        };

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
            Duration = duration,
            FidelityVerdict = MigrationFidelityVerdicts.Incomplete
        };
}
