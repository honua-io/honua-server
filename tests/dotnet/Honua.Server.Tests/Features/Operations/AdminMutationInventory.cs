// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Tests.Features.OperationsToolset;

internal static class AdminMutationInventory
{
    internal enum Classification
    {
        ReadOnlyPost,
        ControlPlaneOrAuth,
        MigrationBacklog,
    }

    internal sealed record Entry(string OpenApiOperationId, Classification Category, string Reason);

    public static IReadOnlyList<Entry> UnprojectedNonGet { get; } =
    [
        new("acknowledgeObservabilityAlert", Classification.MigrationBacklog, "Pending ADR 0089 migration (Observability family)."),
        new("activateGpProcessRelease", Classification.MigrationBacklog, "Pending ADR 0089 migration (Geoprocessing family)."),
        new("addClientCertificateRevocation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Security family)."),
        new("addInvestigationLink", Classification.MigrationBacklog, "Pending ADR 0089 migration (Investigations family)."),
        new("addInvestigationPin", Classification.MigrationBacklog, "Pending ADR 0089 migration (Investigations family)."),
        new("allocateNetworkTopologyDraftGeneration", Classification.MigrationBacklog, "Pending ADR 0089 migration (Network Datasets family)."),
        new("applyNetworkTopologyEditBatch", Classification.MigrationBacklog, "Pending ADR 0089 migration (Network Datasets family)."),
        new("approveCoordinatedReleaseGate", Classification.ControlPlaneOrAuth, "Human coordinated-release gate approval; cannot be agent-approved."),
        new("approveOperationProposal", Classification.ControlPlaneOrAuth, "Human proposal approval gate; cannot be agent-approved."),
        new("assignFieldWorkflowSubmission", Classification.MigrationBacklog, "Pending ADR 0089 migration (Field Workflows family)."),
        new("attestObservabilityAuditGap", Classification.MigrationBacklog, "Pending ADR 0089 migration (Observability family)."),
        new("cancelCompatibilityImportJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("cancelConsoleJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Jobs family)."),
        new("cancelGeoServerImportJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("cancelGeoservicesImportJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("cancelImportUpload", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("cancelMigrationRun", Classification.MigrationBacklog, "Pending ADR 0089 migration (Migration family)."),
        new("cancelOperation", Classification.ControlPlaneOrAuth, "Operation runtime control endpoint; outside governed operation catalog."),
        new("cancelTileOperationJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Tile Operations family)."),
        new("commentFieldWorkflowSubmission", Classification.MigrationBacklog, "Pending ADR 0089 migration (Field Workflows family)."),
        new("compatibilityImportFileFromUrl", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("completeMigrationRun", Classification.MigrationBacklog, "Pending ADR 0089 migration (Migration family)."),
        new("convergePlatformRelease", Classification.MigrationBacklog, "Pending ADR 0089 migration (Platform Release family)."),
        new("createAdminAuthAuthorizeUrl", Classification.ControlPlaneOrAuth, "Interactive OIDC authorization redirect; outside operation catalog."),
        new("createAdminPasswordBearer", Classification.ControlPlaneOrAuth, "Bootstrap operator bearer issuance; outside operation catalog."),
        new("createAdminPasswordSession", Classification.ControlPlaneOrAuth, "Interactive operator password login; outside operation catalog."),
        new("createAlertRule", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("createAlertZone", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("createClientCertificatePrincipalMapping", Classification.MigrationBacklog, "Pending ADR 0089 migration (Security family)."),
        new("createClientCertificateTrustProfile", Classification.MigrationBacklog, "Pending ADR 0089 migration (Security family)."),
        new("createCoordinatedReleaseOperation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Metadata family)."),
        new("createDeployOperation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Deploy family)."),
        new("createEmbedKey", Classification.ControlPlaneOrAuth, "Embed key secret creation; returns plaintext secret material."),
        new("createFieldWorkflowExport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Field Workflows family)."),
        new("createFormDraft", Classification.MigrationBacklog, "Pending ADR 0089 migration (Forms family)."),
        new("createInvestigation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Investigations family)."),
        new("createObjectStorageConnectionSetup", Classification.MigrationBacklog, "Pending ADR 0089 migration (Connections family)."),
        new("createShareExport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Share family)."),
        new("deactivateSceneDataset", Classification.MigrationBacklog, "Pending ADR 0089 migration (Scenes family)."),
        new("decideFieldWorkflowSubmission", Classification.MigrationBacklog, "Pending ADR 0089 migration (Field Workflows family)."),
        new("deleteAgentAuthorityCeiling", Classification.ControlPlaneOrAuth, "AI-admission self-governance ceiling; excluded from agent projection (ADR 0089)."),
        new("deleteAlertRule", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("deleteAlertZone", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("deleteImportedRaster", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("deleteNetworkDataset", Classification.MigrationBacklog, "Pending ADR 0089 migration (Network Datasets family)."),
        new("deleteShareExport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Share family)."),
        new("disableClientCertificatePrincipalMapping", Classification.MigrationBacklog, "Pending ADR 0089 migration (Security family)."),
        new("disableClientCertificateTrustProfile", Classification.MigrationBacklog, "Pending ADR 0089 migration (Security family)."),
        new("disconnectFeatureStreamSession", Classification.MigrationBacklog, "Pending ADR 0089 migration (Streaming family)."),
        new("disconnectOperationsStreamingSubscriber", Classification.MigrationBacklog, "Pending ADR 0089 migration (Operations family)."),
        new("discoverExternalService", Classification.ReadOnlyPost, "Read-only external service discovery; persists no state."),
        new("discoverGeoServerService", Classification.ReadOnlyPost, "Read-only GeoServer discovery; persists no state."),
        new("discoverGeoservicesService", Classification.ReadOnlyPost, "Read-only GeoServices discovery; persists no state."),
        new("evaluateResidencyPolicy", Classification.ReadOnlyPost, "Read-only residency policy evaluation; persists no state."),
        new("exportOgcTileCache", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("generateScene", Classification.MigrationBacklog, "Pending ADR 0089 migration (Scenes family)."),
        new("goLiveFormDraft", Classification.MigrationBacklog, "Pending ADR 0089 migration (Forms family)."),
        new("importFormXlsForm", Classification.MigrationBacklog, "Pending ADR 0089 migration (Forms family)."),
        new("importGeocoderReferenceData", Classification.MigrationBacklog, "Pending ADR 0089 migration (Geocoding family)."),
        new("importOgcApiFeaturesCollection", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("importOgcCoverages", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("importOgcWcsCoverages", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("importRasterFile", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("importTileCachePackage", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("ingestCityGmlScene", Classification.MigrationBacklog, "Pending ADR 0089 migration (Scenes family)."),
        new("ingestPointCloudScene", Classification.MigrationBacklog, "Pending ADR 0089 migration (Scenes family)."),
        new("invalidateEnhancedQueryResultCache", Classification.MigrationBacklog, "Pending ADR 0089 migration (Performance family)."),
        new("invalidateOperationsCache", Classification.MigrationBacklog, "Pending ADR 0089 migration (Operations family)."),
        new("issueAdminOperatorBearer", Classification.ControlPlaneOrAuth, "Operator authentication token issuance; outside operation catalog."),
        new("logoutAdminAuthSession", Classification.ControlPlaneOrAuth, "Interactive operator session logout; outside operation catalog."),
        new("pauseAlertChannel", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("pauseShareExport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Share family)."),
        new("planDeployOperation", Classification.ReadOnlyPost, "Read-only deployment plan preview; persists no state."),
        new("previewPackage", Classification.ReadOnlyPost, "Read-only package preview via PackageReviewService (ADR 0089)."),
        new("promoteDeployOperation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Deploy family)."),
        new("promoteNetworkTopologyGeneration", Classification.MigrationBacklog, "Pending ADR 0089 migration (Network Datasets family)."),
        new("proposeOpsFinding", Classification.MigrationBacklog, "Pending ADR 0089 migration (Observability family)."),
        new("publishCogArtifact", Classification.MigrationBacklog, "Pending ADR 0089 migration (Cloud Rasters family)."),
        new("publishGpProcessRelease", Classification.MigrationBacklog, "Pending ADR 0089 migration (Geoprocessing family)."),
        new("publishMapPackage", Classification.MigrationBacklog, "Pending ADR 0089 migration (Packages family)."),
        new("queryMetadataEnvironmentBindings", Classification.ReadOnlyPost, "Read-only environment binding query; persists no state."),
        new("recordGeoServicesMigrationManifest", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("recordGeoServicesMigrationParity", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("recordMigrationRunScorecard", Classification.MigrationBacklog, "Pending ADR 0089 migration (Migration family)."),
        new("recordMigrationRunStarted", Classification.MigrationBacklog, "Pending ADR 0089 migration (Migration family)."),
        new("redriveAlertDeadLetters", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("refreshCloudRaster", Classification.MigrationBacklog, "Pending ADR 0089 migration (Cloud Rasters family)."),
        new("refreshMultidimCoverage", Classification.MigrationBacklog, "Pending ADR 0089 migration (Multidimensional Coverages family)."),
        new("refreshZarrStore", Classification.MigrationBacklog, "Pending ADR 0089 migration (Zarr Stores family)."),
        new("registerCloudRaster", Classification.MigrationBacklog, "Pending ADR 0089 migration (Cloud Rasters family)."),
        new("registerMultidimCoverage", Classification.MigrationBacklog, "Pending ADR 0089 migration (Multidimensional Coverages family)."),
        new("registerNetworkDataset", Classification.MigrationBacklog, "Pending ADR 0089 migration (Network Datasets family)."),
        new("registerSceneDataset", Classification.MigrationBacklog, "Pending ADR 0089 migration (Scenes family)."),
        new("registerZarrStore", Classification.MigrationBacklog, "Pending ADR 0089 migration (Zarr Stores family)."),
        new("rejectOperationProposal", Classification.ControlPlaneOrAuth, "Human proposal rejection gate; cannot be agent-approved."),
        new("removeClientCertificateRevocation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Security family)."),
        new("removeInvestigationLink", Classification.MigrationBacklog, "Pending ADR 0089 migration (Investigations family)."),
        new("removeInvestigationPin", Classification.MigrationBacklog, "Pending ADR 0089 migration (Investigations family)."),
        new("reopenFormVersion", Classification.MigrationBacklog, "Pending ADR 0089 migration (Forms family)."),
        new("requestAdminAuthToken", Classification.ControlPlaneOrAuth, "Interactive OIDC token exchange; outside operation catalog."),
        new("resetFeatureStreamConformanceSource", Classification.MigrationBacklog, "Pending ADR 0089 migration (Streaming family)."),
        new("resolveObservabilityAlert", Classification.MigrationBacklog, "Pending ADR 0089 migration (Observability family)."),
        new("resolveServiceReplicaConflict", Classification.MigrationBacklog, "Pending ADR 0089 migration (Services family)."),
        new("resumeAlertChannel", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("resumeShareExport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Share family)."),
        new("retryConsoleJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Jobs family)."),
        new("retryTileOperationJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Tile Operations family)."),
        new("revokeEmbedKey", Classification.ControlPlaneOrAuth, "Embed key revocation; operator-only secret lifecycle."),
        new("rollbackDeployOperation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Deploy family)."),
        new("rollbackNetworkTopologyGeneration", Classification.MigrationBacklog, "Pending ADR 0089 migration (Network Datasets family)."),
        new("rotateComplianceEncryptionKey", Classification.ControlPlaneOrAuth, "Encryption master-key rotation; operator-only secret lifecycle."),
        new("rotateConnectionEncryptionKey", Classification.ControlPlaneOrAuth, "Connection encryption master-key rotation; operator-only secret lifecycle."),
        new("rotateEmbedKey", Classification.ControlPlaneOrAuth, "Embed key rotation; returns plaintext secret material."),
        new("scanEnhancedResourceLeaks", Classification.ReadOnlyPost, "Read-only diagnostic leak scan; persists no state."),
        new("scanMigrationSource", Classification.ReadOnlyPost, "Read-only migration source scan; persists no state."),
        new("setAdminLayerRelationships", Classification.MigrationBacklog, "Pending ADR 0089 migration (Metadata family)."),
        new("setAdminLayerRelationshipsBatch", Classification.MigrationBacklog, "Pending ADR 0089 migration (Metadata family)."),
        new("setAgentAuthorityCeiling", Classification.ControlPlaneOrAuth, "AI-admission self-governance ceiling; excluded from agent projection (ADR 0089)."),
        new("setAlertRuleEnabled", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("setOpsAutonomyPolicy", Classification.MigrationBacklog, "Pending ADR 0089 migration (Observability family)."),
        new("setOpsAutonomySettings", Classification.MigrationBacklog, "Pending ADR 0089 migration (Observability family)."),
        new("setServiceLayersEnabled", Classification.MigrationBacklog, "Pending ADR 0089 migration (Layers family)."),
        new("startGeoServerImport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("startGeoservicesImport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("startMigrationBatch", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("startOgcWfsImport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("startTileOperationJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Tile Operations family)."),
        new("submitDeployOperation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Deploy family)."),
        new("submitGpProcessReleaseJob", Classification.MigrationBacklog, "Pending ADR 0089 migration (Geoprocessing family)."),
        new("submitNetworkTopologyRebuild", Classification.MigrationBacklog, "Pending ADR 0089 migration (Network Datasets family)."),
        new("suggestLayerStyle", Classification.ReadOnlyPost, "Read-only style suggestion; persists no state."),
        new("suppressObservabilityAlert", Classification.MigrationBacklog, "Pending ADR 0089 migration (Observability family)."),
        new("testAlertRule", Classification.ReadOnlyPost, "Read-only draft alert rule validation; persists no state (ADR 0089)."),
        new("triggerShareExport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Share family)."),
        new("unregisterCloudRaster", Classification.MigrationBacklog, "Pending ADR 0089 migration (Cloud Rasters family)."),
        new("unregisterMultidimCoverage", Classification.MigrationBacklog, "Pending ADR 0089 migration (Multidimensional Coverages family)."),
        new("unregisterZarrStore", Classification.MigrationBacklog, "Pending ADR 0089 migration (Zarr Stores family)."),
        new("updateAdminLayerDiscovery", Classification.MigrationBacklog, "Pending ADR 0089 migration (Metadata family)."),
        new("updateAdminServiceDiscovery", Classification.MigrationBacklog, "Pending ADR 0089 migration (Metadata family)."),
        new("updateAlertRule", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("updateAlertZone", Classification.MigrationBacklog, "Pending ADR 0089 migration (Alerts family)."),
        new("updateClientCertificatePrincipalMapping", Classification.MigrationBacklog, "Pending ADR 0089 migration (Security family)."),
        new("updateClientCertificateTrustProfile", Classification.MigrationBacklog, "Pending ADR 0089 migration (Security family)."),
        new("updateFormDraft", Classification.MigrationBacklog, "Pending ADR 0089 migration (Forms family)."),
        new("updateImportedRaster", Classification.MigrationBacklog, "Pending ADR 0089 migration (Import family)."),
        new("updateInvestigation", Classification.MigrationBacklog, "Pending ADR 0089 migration (Investigations family)."),
        new("updateNetworkDataset", Classification.MigrationBacklog, "Pending ADR 0089 migration (Network Datasets family)."),
        new("updateSceneDataset", Classification.MigrationBacklog, "Pending ADR 0089 migration (Scenes family)."),
        new("updateServiceMapServer", Classification.MigrationBacklog, "Pending ADR 0089 migration (Services family)."),
        new("updateShareExport", Classification.MigrationBacklog, "Pending ADR 0089 migration (Share family)."),
        new("uploadLicense", Classification.ControlPlaneOrAuth, "Legacy operator license upload; admin.license.upload covers canonical upload."),
        new("validateClientCertificate", Classification.ReadOnlyPost, "Read-only client certificate validation; persists no state."),
        new("validateConnectionEncryption", Classification.ReadOnlyPost, "Read-only connection encryption validation; persists no state."),
        new("validateFormDraft", Classification.MigrationBacklog, "Pending ADR 0089 migration (Forms family)."),
        new("validatePackage", Classification.ReadOnlyPost, "Read-only package validation via PackageReviewService (ADR 0089)."),
        new("validateToolboxTranslation", Classification.ReadOnlyPost, "Read-only toolbox translation validation; persists no state."),
    ];

    public static IReadOnlyList<string> Evaluate(
        JsonElement contract,
        IReadOnlySet<string> projectedOpenApiIds,
        IReadOnlyList<Entry> entries)
    {
        var findings = new List<string>();
        var operations = AdminAccessOperationDriftGate.ReadOperations(contract)
            .ToDictionary(static op => op.OperationId, StringComparer.Ordinal);
        var entryMap = IndexEntries(entries, findings);

        foreach (var op in operations.Values.Where(static op => op.Method is not ("GET" or "HEAD" or "OPTIONS")))
        {
            var inCatalog = projectedOpenApiIds.Contains(op.OperationId);
            var inInventory = entryMap.ContainsKey(op.OperationId);
            if (!inCatalog && !inInventory)
            {
                findings.Add($"unclassified: non-GET admin operation '{op.OperationId}' ({op.Method} {op.Path}) is neither projected nor classified.");
            }
            else if (inCatalog && inInventory)
            {
                findings.Add($"duplicate-classification: '{op.OperationId}' is projected in an admin catalog and still listed as unprojected.");
            }
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Reason))
            {
                findings.Add($"missing-reason: '{entry.OpenApiOperationId}' has no classification reason.");
            }

            if (!operations.TryGetValue(entry.OpenApiOperationId, out var op))
            {
                findings.Add($"stale-inventory: '{entry.OpenApiOperationId}' is no longer in admin-api.json.");
            }
            else if (op.Method is "GET" or "HEAD" or "OPTIONS")
            {
                findings.Add($"read-only-method: '{entry.OpenApiOperationId}' uses {op.Method} and does not belong in the non-GET inventory.");
            }
        }

        return findings;
    }

    private static Dictionary<string, Entry> IndexEntries(IReadOnlyList<Entry> entries, List<string> findings)
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!map.TryAdd(entry.OpenApiOperationId, entry))
            {
                findings.Add($"duplicate-inventory: '{entry.OpenApiOperationId}' is listed more than once.");
            }
        }

        return map;
    }
}
