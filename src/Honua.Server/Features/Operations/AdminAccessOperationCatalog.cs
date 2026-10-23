// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>Access-family operations projected from the shipped Admin OpenAPI contract.</summary>
internal static class AdminAccessOperationCatalog
{
    internal sealed record Definition(
        string OperationId,
        string Title,
        HttpMethod Method,
        string Path,
        string OpenApiOperationId,
        OperationSideEffectClass SideEffect,
        OperationBlastRadiusClass BlastRadius = OperationBlastRadiusClass.ResourceScope,
        bool SupportsDryRun = false,
        string? DryRunPath = null,
        HttpMethod? DryRunMethod = null,
        string? ContentType = null,
        OperationApprovalModel? ApprovalModel = null,
        OperationClass OperationClass = OperationClass.AdminConfigChange) : IAdminHttpOperationDefinition;

    public static IReadOnlyList<Definition> Definitions { get; } =
    [
        Read("admin.api-key.list", "List Admin API keys", "/api-keys", "listAdminApiKeys"),
        Write("admin.api-key.create", "Create Admin API key", HttpMethod.Post, "/api-keys", "createAdminApiKey"),
        Write("admin.api-key.rotate", "Rotate Admin API key", HttpMethod.Post, "/api-keys/{id}/rotate", "rotateAdminApiKey"),
        Write("admin.api-key.revoke", "Revoke Admin API key", HttpMethod.Post, "/api-keys/{id}/revoke", "revokeAdminApiKey", OperationSideEffectClass.DestroysState),
        Read("admin.api-key.effective-permissions", "Get Admin API key effective permissions", "/api-keys/{id}/effective-permissions", "getAdminApiKeyEffectivePermissions"),

        Read("admin.role.list", "List roles", "/roles", "listRoles"),
        Write("admin.role.create", "Create role", HttpMethod.Post, "/roles", "createRole"),
        Read("admin.role.get", "Get role", "/roles/{id}", "getRole"),
        Write("admin.role.update", "Update role", HttpMethod.Put, "/roles/{id}", "updateRole"),
        Write("admin.role.delete", "Delete role", HttpMethod.Delete, "/roles/{id}", "deleteRole", OperationSideEffectClass.DestroysState),
        Read("admin.role.permissions.get", "Get role permissions", "/roles/{id}/permissions", "getRolePermissions"),
        Write("admin.role.permissions.set", "Set role permissions", HttpMethod.Put, "/roles/{id}/permissions", "setRolePermissions"),

        Read("admin.user.list", "List users", "/users", "listManagedUsers"),
        Read("admin.user.get", "Get user", "/users/{id}", "getManagedUser"),
        Write("admin.user.deprovision", "Deprovision user", HttpMethod.Delete, "/users/{id}", "deprovisionManagedUser", OperationSideEffectClass.DestroysState),
        Write("admin.user.roles.update", "Update user roles", HttpMethod.Put, "/users/{id}/roles", "updateManagedUserRoles"),
        Read("admin.user.effective-permissions", "Get user effective permissions", "/users/{id}/effective-permissions", "getManagedUserEffectivePermissions"),

        Read("admin.tenant.list", "List tenants", "/tenants", "listTenants"),
        Write("admin.tenant.create", "Create tenant", HttpMethod.Post, "/tenants", "createTenant"),
        Read("admin.tenant.usage.export", "Export tenant billing usage", "/tenants/usage", "exportTenantUsage"),
        Read("admin.tenant.get", "Get tenant", "/tenants/{tenantId}", "getTenant"),
        Write("admin.tenant.delete", "Delete tenant", HttpMethod.Delete, "/tenants/{tenantId}", "deleteTenant", OperationSideEffectClass.DestroysState),
        Write("admin.tenant.suspend", "Suspend tenant", HttpMethod.Post, "/tenants/{tenantId}/suspend", "suspendTenant"),
        Write("admin.tenant.resume", "Resume tenant", HttpMethod.Post, "/tenants/{tenantId}/resume", "resumeTenant"),

        Read("admin.oidc-provider.list", "List OIDC providers", "/oidc/providers", "listOidcProviders"),
        Write("admin.oidc-provider.create", "Create OIDC provider", HttpMethod.Post, "/oidc/providers", "createOidcProvider"),
        Read("admin.oidc-provider.get", "Get OIDC provider", "/oidc/providers/{id}", "getOidcProvider"),
        Write("admin.oidc-provider.update", "Update OIDC provider", HttpMethod.Put, "/oidc/providers/{id}", "updateOidcProvider"),
        Write("admin.oidc-provider.delete", "Delete OIDC provider", HttpMethod.Delete, "/oidc/providers/{id}", "deleteOidcProvider", OperationSideEffectClass.DestroysState),
        Read("admin.oidc-provider.test", "Test OIDC provider connection", HttpMethod.Post, "/oidc/providers/{id}/test", "testOidcProvider"),

        Read("admin.oauth-client.list", "List OAuth clients", "/oauth-clients", "listOAuthClients"),
        Write("admin.oauth-client.register", "Register OAuth client", HttpMethod.Post, "/oauth-clients", "registerOAuthClient"),
        Read("admin.oauth-client.get", "Get OAuth client", "/oauth-clients/{id}", "getOAuthClient"),
        Write("admin.oauth-client.delete", "Delete OAuth client", HttpMethod.Delete, "/oauth-clients/{id}", "deleteOAuthClient", OperationSideEffectClass.DestroysState),
        Read("admin.oauth-scope.list", "List OAuth scopes", "/oauth-scopes", "listOAuthScopes"),
        Write("admin.oauth-scope.define", "Define OAuth scope", HttpMethod.Put, "/oauth-scopes", "defineOAuthScope"),
        Write("admin.oauth-scope.delete", "Delete OAuth scope", HttpMethod.Delete, "/oauth-scopes/{scope}", "deleteOAuthScope", OperationSideEffectClass.DestroysState),

        Read("admin.rate-limit-policy.list", "List rate-limit policies", "/rate-limits", "listRateLimitPolicies"),
        Write("admin.rate-limit-policy.create", "Create rate-limit policy", HttpMethod.Post, "/rate-limits", "createRateLimitPolicy"),
        Read("admin.rate-limit-policy.status", "Get rate-limit status", "/rate-limits/status", "getRateLimitStatus"),
        Read("admin.rate-limit-policy.get", "Get rate-limit policy", "/rate-limits/{id}", "getRateLimitPolicy"),
        Write("admin.rate-limit-policy.update", "Update rate-limit policy", HttpMethod.Put, "/rate-limits/{id}", "updateRateLimitPolicy"),
        Write("admin.rate-limit-policy.delete", "Delete rate-limit policy", HttpMethod.Delete, "/rate-limits/{id}", "deleteRateLimitPolicy", OperationSideEffectClass.DestroysState),

        Read("admin.field-mask-policy.list", "List field-mask policies", "/field-mask-policies", "listFieldMaskPolicies"),
        Write("admin.field-mask-policy.create", "Create field-mask policy", HttpMethod.Post, "/field-mask-policies", "createFieldMaskPolicy"),
        Read("admin.field-mask-policy.get", "Get field-mask policy", "/field-mask-policies/{id}", "getFieldMaskPolicy"),
        Write("admin.field-mask-policy.delete", "Delete field-mask policy", HttpMethod.Delete, "/field-mask-policies/{id}", "deleteFieldMaskPolicy", OperationSideEffectClass.DestroysState),

        Read("admin.rls-policy.list", "List RLS policies", "/rls-policies", "listRlsPolicies"),
        Write("admin.rls-policy.create", "Create RLS policy", HttpMethod.Post, "/rls-policies", "createRlsPolicy"),
        Read("admin.rls-policy.get", "Get RLS policy", "/rls-policies/{id}", "getRlsPolicy"),
        Write("admin.rls-policy.delete", "Delete RLS policy", HttpMethod.Delete, "/rls-policies/{id}", "deleteRlsPolicy", OperationSideEffectClass.DestroysState),
    ];

    public static IReadOnlyList<OperationDescriptor> Descriptors { get; } = BuildDescriptors();

    private static Definition Read(string id, string title, string path, string openApiId) =>
        new(id, title, HttpMethod.Get, path, openApiId, OperationSideEffectClass.ReadOnly);

    private static Definition Read(string id, string title, HttpMethod method, string path, string openApiId) =>
        new(id, title, method, path, openApiId, OperationSideEffectClass.ReadOnly);

    private static Definition Write(string id, string title, HttpMethod method, string path, string openApiId,
        OperationSideEffectClass sideEffect = OperationSideEffectClass.MutatesMetadata) =>
        new(id, title, method, path, openApiId, sideEffect);

    private static OperationDescriptor[] BuildDescriptors()
    {
        using var stream = typeof(AdminAccessOperationCatalog).Assembly
            .GetManifestResourceStream("Honua.Server.admin-api.json")
            ?? throw new InvalidOperationException("Embedded admin-api.json contract was not found.");
        using var document = JsonDocument.Parse(stream);
        return Definitions.Select(definition =>
            AdminOperateOperationCatalog.BuildDescriptor(document.RootElement, definition)).ToArray();
    }
}

internal sealed class AdminAccessOperationDescriptorProvider : IOperationDescriptorProvider
{
    public string ProviderId => ServicePublishOperation.ProviderId;

    public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IOperationDescriptor>>(AdminAccessOperationCatalog.Descriptors);
}
