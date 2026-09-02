// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Operations.Services;

/// <summary>Audited Admin operations that remain REST/CLI-only and must never publish over MCP.</summary>
public static class AdminMcpOperationExclusions
{
    /// <summary>Stable exclusion entry joined to both the Admin OpenAPI and operation catalog identities.</summary>
    public sealed record Entry(
        string OpenApiOperationId,
        string OperationId,
        string ToolName,
        string ReasonCode,
        string Explanation);

    /// <summary>The complete one-time-secret and browser-session exclusion roster.</summary>
    public static IReadOnlyList<Entry> All { get; } =
    [
        Secret("createAdminApiKey", "admin.api-key.create", "honua_admin_api_key_create", "Creates one-time API-key secret material."),
        Secret("rotateAdminApiKey", "admin.api-key.rotate", "honua_admin_api_key_rotate", "Returns a newly rotated one-time API-key secret."),
        Secret("createEmbedKey", "admin.embed-key.create", "honua_admin_embed_key_create", "Creates one-time embed-key secret material."),
        Secret("rotateEmbedKey", "admin.embed-key.rotate", "honua_admin_embed_key_rotate", "Returns a newly rotated one-time embed-key secret."),
        Secret("registerOAuthClient", "admin.oauth-client.register", "honua_admin_oauth_client_register", "Returns a newly issued OAuth client secret."),
        Session("getAdminAuthSession", "admin.auth-session.get", "honua_admin_auth_session_get", "Reads browser-session-bound authentication state."),
        Session("issueAdminOperatorBearer", "admin.auth-session.issue-bearer", "honua_admin_auth_session_issue_bearer", "Issues a bearer from browser-session authority."),
        Session("logoutAdminAuthSession", "admin.auth-session.logout", "honua_admin_auth_session_logout", "Mutates the caller's browser-bound session."),
        Session("createAdminAuthAuthorizeUrl", "admin.auth-session.authorize-url", "honua_admin_auth_session_authorize_url", "Creates a browser-session-bound authorization flow."),
        Session("requestAdminAuthToken", "admin.auth-session.request-token", "honua_admin_auth_session_request_token", "Exchanges browser authentication state for secret token material."),
        Session("getAdminAuthLogoutUrl", "admin.auth-session.logout-url", "honua_admin_auth_session_logout_url", "Creates a browser-session-bound logout flow."),
    ];

    /// <summary>Digest of the ordered full roster, for receipts and downstream equality checks.</summary>
    public static string Digest { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', All.Select(entry =>
            $"{entry.OpenApiOperationId}|{entry.OperationId}|{entry.ToolName}|{entry.ReasonCode}|{entry.Explanation}")))));

    /// <summary>Returns whether an operation is deliberately absent from MCP.</summary>
    public static bool ContainsOperation(string operationId) =>
        All.Any(entry => string.Equals(entry.OperationId, operationId, StringComparison.Ordinal));

    private static Entry Secret(string openApiId, string operationId, string toolName, string explanation) =>
        new(openApiId, operationId, toolName, "one-time-secret", explanation);

    private static Entry Session(string openApiId, string operationId, string toolName, string explanation) =>
        new(openApiId, operationId, toolName, "browser-session-bound", explanation);
}
