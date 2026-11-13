// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpInputDescriptions
{
    private static readonly Dictionary<string, string> IdentityLists = new(StringComparer.Ordinal)
    {
        ["api-key"] = "honua_admin_api_key_list",
        ["role"] = "honua_admin_role_list",
        ["user"] = "honua_admin_user_list",
        ["oidc-provider"] = "honua_admin_oidc_provider_list",
        ["oauth-client"] = "honua_admin_oauth_client_list",
        ["field-mask-policy"] = "honua_admin_field_mask_policy_list",
        ["rls-policy"] = "honua_admin_rls_policy_list",
        ["rate-limit-policy"] = "honua_admin_rate_limit_policy_list",
    };

    private static readonly HashSet<(string Operation, string Parameter)> CallerInputs =
    [
        ("admin.connections.create", "name"), ("admin.connections.create", "host"),
        ("admin.connections.create", "databaseName"), ("admin.connections.create", "username"),
        ("admin.import.upload-object", "sourceUri"), ("admin.import.upload-object", "tableName"),
        ("admin.import.upload-url", "sourceUrl"), ("admin.import.upload-url", "tableName"),
        ("admin.layer.publish", "layerName"), ("admin.layer.set-enabled", "enabled"),
        ("admin.layer.drawing-info.set", "body"), ("admin.layer.popup-info.set", "body"),
        ("admin.layer.style.import-sld", "body"), ("admin.oauth-scope.define", "scope"),
        ("admin.rate-limit-policy.create", "key"), ("admin.rate-limit-policy.create", "name"),
        ("admin.rate-limit-policy.create", "scope"), ("admin.rate-limit-policy.create", "requestsPerWindow"),
        ("admin.rate-limit-policy.create", "windowDurationSeconds"), ("admin.rate-limit-policy.status", "key"),
        ("admin.rls-policy.create", "claimType"), ("admin.role.create", "name"),
        ("admin.role.permissions.set", "permissions"), ("admin.services.protocols.set", "enabledProtocols"),
        ("admin.tenant.create", "tenantId"), ("folder.create", "name"),
    ];

    internal static string Describe(string operationId, string parameter, string description)
    {
        var source = ReadSource(operationId, parameter);
        if (source is not null) return $"{description} Source: {source}.";
        return CallerInputs.Contains((operationId, parameter)) ? $"Caller-supplied. {description}" : description;
    }

    private static string? ReadSource(string operationId, string parameter)
    {
        if (parameter == "id") return IdentitySource(operationId);
        return parameter switch
        {
            "connectionId" => "honua_admin_connections_list",
            "serviceName" or "service" => PublishedOperationTool.ProjectName("admin.services.list"),
            "layerId" when operationId == "admin.layer.set-enabled" => "honua_admin_connection_layers_list (storage layerId)",
            "layerId" => "honua_list_layers (publication layerId)",
            "layer" => "honua_list_layers (or caller-selected policy wildcard)",
            "role" or "roles" => "honua_admin_role_list",
            "attribute" => "honua_describe_layer (field name)",
            "tenantId" when operationId != "admin.tenant.create" => "honua_admin_tenant_list",
            "scope" when operationId == "admin.oauth-scope.delete" => "honua_admin_oauth_scope_list",
            "schema" or "table" when operationId == "admin.layer.publish" => "honua_admin_connection_tables_list",
            "folderId" or "If-Match" => PublishedOperationTool.ProjectName("folder.list"),
            _ => null
        };
    }

    private static string? IdentitySource(string operationId)
    {
        if (operationId.StartsWith("folder.", StringComparison.Ordinal)) return PublishedOperationTool.ProjectName("folder.list");
        if (operationId.StartsWith("admin.connections.", StringComparison.Ordinal)) return "honua_admin_connections_list";
        var parts = operationId.Split('.');
        return parts.Length > 2 && IdentityLists.TryGetValue(parts[1], out var source) ? source : null;
    }
}
