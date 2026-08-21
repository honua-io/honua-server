// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Operations.Admin;

/// <summary>
/// Semantic inventory of the 2026.1 admin control-plane operations. HTTP paths, methods,
/// titles, descriptions, and JSON schemas are deliberately absent: they are resolved from
/// <c>admin-openapi.json</c> by <see cref="AdminOpenApiOperationCatalog"/>.
/// </summary>
internal static class AdminOperationManifest
{
    public const string ProviderId = "honua.server.admin-operations";

    public static IReadOnlyList<AdminOperationManifestEntry> All { get; } =
    [
        // Lane A: connect + import (#3359).
        Op("admin.connection.list", "getConnections", "A"),
        Op("admin.connection.create", "createConnection", "A", "admin.connection.test-draft"),
        Op("admin.connection.get", "getConnection", "A"),
        Op("admin.connection.update", "updateConnection", "A", "admin.connection.test"),
        Op("admin.connection.delete", "deleteConnection", "A"),
        Op("admin.connection.test", "testConnection", "A"),
        Op("admin.connection.test-draft", "testDraftConnection", "A"),
        Op("admin.connection.discover-tables", "getConnectionTables", "A"),
        Op("admin.connection.validate-table", "validateConnectionTableForPublish", "A"),
        Op("admin.connection.refresh-extents", "refreshConnectionLayerExtents", "A"),
        Op("admin.connection.refresh-features", "refreshConnectionLayerFeatures", "A"),
        Op("admin.import.upload-file", "uploadImportFile", "A"),
        Op("admin.import.upload-url", "uploadImportFileFromUrl", "A"),
        Op("admin.import.preview-file", "previewImportFile", "A"),
        Op("admin.import.preview-url", "previewImportFileFromUrl", "A"),
        Op("admin.import.formats", "getImportFormats", "A"),
        Op("admin.import.limits", "getImportLimits", "A"),
        Op("admin.import.list-jobs", "getActiveImportJobs", "A"),
        Op("admin.import.get-job", "getImportJobStatus", "A"),
        Op("admin.import.cancel-job", "cancelImportJob", "A"),
        Op("admin.import.list-uploads", "listActiveImportUploads", "A"),
        Op("admin.import.get-upload-progress", "getImportUploadProgress", "A"),
        Op("admin.import.cancel-upload", "cancelImportUpload", "A"),

        // Lane B: publish + layer config + services (#3360).
        Op("admin.layer.publish", "publishLayer", "B", "admin.connection.validate-table"),
        Op("admin.layer.set-enabled", "setLayerEnabled", "B"),
        Op("admin.layer.get-fields", "getAdminLayerFields", "B"),
        Op("admin.layer.set-fields", "updateAdminLayerFields", "B"),
        Op("admin.layer.get-filter", "getAdminLayerFilter", "B"),
        Op("admin.layer.set-filter", "updateAdminLayerFilter", "B"),
        Op("admin.layer.get-popup-info", "getAdminLayerPopupInfo", "B"),
        Op("admin.layer.set-popup-info", "setAdminLayerPopupInfo", "B"),
        Op("admin.layer.get-drawing-info", "getAdminLayerDrawingInfo", "B"),
        Op("admin.layer.set-drawing-info", "setAdminLayerDrawingInfo", "B"),
        Op("admin.layer.get-style", "getAdminLayerStyle", "B"),
        Op("admin.layer.set-style", "updateAdminLayerStyle", "B"),
        Op("admin.layer.import-sld", "importLayerSldStyle", "B"),
        Op("admin.layer.export-sld", "exportLayerSldStyle", "B"),
        Op("admin.service.list", "listServices", "B"),
        Op("admin.service.get-settings", "getServiceSettings", "B"),
        Op("admin.service.set-protocols", "updateServiceProtocols", "B"),
        Op("admin.service.set-mapserver", "updateServiceMapServer", "B"),
        Op("admin.service.set-access-policy", "updateServiceAccessPolicy", "B"),
        Op("admin.service.set-time-info", "updateServiceTimeInfo", "B"),
        Op("admin.service.set-layer-metadata", "updateLayerMetadata", "B"),

        // Lane C: access (#3361).
        Op("admin.api-key.list", "listAdminApiKeys", "C"),
        Op("admin.api-key.create", "createAdminApiKey", "C"),
        Op("admin.api-key.rotate", "rotateAdminApiKey", "C"),
        Op("admin.api-key.revoke", "revokeAdminApiKey", "C"),
        Op("admin.api-key.effective-permissions", "getAdminApiKeyEffectivePermissions", "C"),
        Op("admin.user.list", "listManagedUsers", "C"),
        Op("admin.user.get", "getManagedUser", "C"),
        Op("admin.user.set-roles", "updateManagedUserRoles", "C"),
        Op("admin.user.delete", "deprovisionManagedUser", "C"),
        Op("admin.role.list", "listRoles", "C"),
        Op("admin.role.create", "createRole", "C"),
        Op("admin.role.get", "getRole", "C"),
        Op("admin.role.update", "updateRole", "C"),
        Op("admin.role.delete", "deleteRole", "C"),
        Op("admin.role.get-permissions", "getRolePermissions", "C"),
        Op("admin.role.set-permissions", "setRolePermissions", "C"),
        Op("admin.oidc-provider.list", "listOidcProviders", "C"),
        Op("admin.oidc-provider.create", "createOidcProvider", "C"),
        Op("admin.oidc-provider.get", "getOidcProvider", "C"),
        Op("admin.oidc-provider.update", "updateOidcProvider", "C"),
        Op("admin.oidc-provider.delete", "deleteOidcProvider", "C"),
        Op("admin.oidc-provider.test", "testOidcProvider", "C"),
        Op("admin.oauth-client.list", "listOAuthClients", "C"),
        Op("admin.oauth-client.create", "registerOAuthClient", "C"),
        Op("admin.oauth-client.get", "getOAuthClient", "C"),
        Op("admin.oauth-client.delete", "deleteOAuthClient", "C"),
        Op("admin.oauth-scope.list", "listOAuthScopes", "C"),
        Op("admin.oauth-scope.define", "defineOAuthScope", "C"),
        Op("admin.oauth-scope.delete", "deleteOAuthScope", "C"),
        Op("admin.rate-limit.list", "listRateLimitPolicies", "C"),
        Op("admin.rate-limit.create", "createRateLimitPolicy", "C"),
        Op("admin.rate-limit.get", "getRateLimitPolicy", "C"),
        Op("admin.rate-limit.update", "updateRateLimitPolicy", "C"),
        Op("admin.rate-limit.delete", "deleteRateLimitPolicy", "C"),
        Op("admin.rate-limit.status", "getRateLimitStatus", "C"),
        Op("admin.rls-policy.list", "listRlsPolicies", "C"),
        Op("admin.rls-policy.create", "createRlsPolicy", "C"),
        Op("admin.rls-policy.get", "getRlsPolicy", "C"),
        Op("admin.rls-policy.delete", "deleteRlsPolicy", "C"),
        Op("admin.field-mask.list", "listFieldMaskPolicies", "C"),
        Op("admin.field-mask.create", "createFieldMaskPolicy", "C"),
        Op("admin.field-mask.get", "getFieldMaskPolicy", "C"),
        Op("admin.field-mask.delete", "deleteFieldMaskPolicy", "C"),
        Op("admin.tenant.list", "listTenants", "C"),
        Op("admin.tenant.create", "createTenant", "C"),
        Op("admin.tenant.get", "getTenant", "C"),
        Op("admin.tenant.suspend", "suspendTenant", "C"),
        Op("admin.tenant.resume", "resumeTenant", "C"),
        Op("admin.tenant.delete", "deleteTenant", "C"),

        // Lane D: release + operate (#3362).
        Op("admin.release-package.create", "createMetadataReleasePackage", "D", "admin.release-package.prevalidate"),
        Op("admin.release-package.list", "listMetadataReleasePackages", "D"),
        Op("admin.release-package.get", "getMetadataReleasePackage", "D"),
        Op("admin.release-package.gitops-manifest", "getMetadataReleaseGitOpsManifest", "D"),
        Op("admin.release-package.prevalidate", "prevalidateMetadataReleasePackageCompatibility", "D"),
        Op("admin.release-package.operation-status", "getMetadataReleaseOperationByPackageId", "D"),
        Op("admin.release.create-operation", "createDeployOperation", "D"),
        Op("admin.release.list-operations", "listDeployOperations", "D"),
        Op("admin.release.get-operation", "getDeployOperation", "D"),
        Op("admin.release.activate", "submitDeployOperation", "D"),
        Op("admin.release.promote", "promoteDeployOperation", "D"),
        Op("admin.release.rollback", "rollbackDeployOperation", "D"),
        Op("admin.coordinated-release.create", "createCoordinatedReleaseOperation", "D"),
        Op("admin.coordinated-release.approve-gate", "approveCoordinatedReleaseGate", "D"),
        Op("admin.coordinated-release.rollback", "rollbackCoordinatedReleaseOperation", "D"),
        Op("admin.coordinated-release.status", "getCoordinatedReleaseOperation", "D"),
        Op("admin.cache.status", "getAdminCacheStatus", "D"),
        Op("admin.cache.invalidate", "invalidateAdminCache", "D"),
        Op("admin.license.status", "getLicenseStatus", "D"),
        Op("admin.license.upload", "uploadLicense", "D"),
        Op("admin.license.entitlements", "getLicenseEntitlements", "D"),
        Op("admin.configuration.summary", "getConfigurationSummary", "D"),
        Op("admin.configuration.validate-secrets", "validateConfigurationSecrets", "D"),
        Op("admin.server.status", "getAdminVersion", "D"),
        Op("admin.server.capabilities", "getAdminCapabilities", "D"),
        Op("admin.server.features", "getFeatureOverview", "D"),
    ];

    public static bool Contains(string operationId)
        => All.Any(entry => string.Equals(entry.OperationId, operationId, StringComparison.Ordinal));

    private static AdminOperationManifestEntry Op(
        string operationId,
        string openApiOperationId,
        string lane,
        string? dryRunOperationId = null)
        => new(operationId, openApiOperationId, lane, dryRunOperationId);
}

/// <summary>One semantic operation bound to the authoritative OpenAPI operation id.</summary>
internal sealed record AdminOperationManifestEntry(
    string OperationId,
    string OpenApiOperationId,
    string Lane,
    string? DryRunOperationId);
