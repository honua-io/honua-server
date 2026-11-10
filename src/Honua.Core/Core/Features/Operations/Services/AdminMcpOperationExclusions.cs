// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Operations.Services;

// These audited admin operations stay on REST and CLI and must never be published over MCP.
public static class AdminMcpOperationExclusions
{
    public const string OneTimeSecretReasonCode = "one-time-secret";
    public const string SecretInputReasonCode = "secret-input";

    public sealed record Entry(
        string OpenApiOperationId,
        string OperationId,
        string ToolName,
        string ReasonCode,
        string Explanation);

    public static IReadOnlyList<Entry> All { get; } =
    [
        Secret("createAdminApiKey", "admin.api-key.create", "honua_admin_api_key_create", "Creates one-time API-key secret material."),
        Secret("rotateAdminApiKey", "admin.api-key.rotate", "honua_admin_api_key_rotate", "Returns a newly rotated one-time API-key secret."),
        Secret("createEmbedKey", "admin.embed-key.create", "honua_admin_embed_key_create", "Creates one-time embed-key secret material."),
        Secret("rotateEmbedKey", "admin.embed-key.rotate", "honua_admin_embed_key_rotate", "Returns a newly rotated one-time embed-key secret."),
        Secret("registerOAuthClient", "admin.oauth-client.register", "honua_admin_oauth_client_register", "Returns a newly issued OAuth client secret."),
        SecretInput("createOidcProvider", "admin.oidc-provider.create", "honua_admin_oidc_provider_create", "Accepts secret-bearing OIDC provider configuration."),
        SecretInput("updateOidcProvider", "admin.oidc-provider.update", "honua_admin_oidc_provider_update", "Accepts secret-bearing OIDC provider configuration."),
        // admin.connections.create is on the closed operator roster. MCP publishes it without the
        // plaintext password field; callers pass secretReference. Update and draft test still accept
        // a password and stay excluded.
        SecretInput("updateConnection", "admin.connections.update", "honua_admin_connections_update", "Accepts a plaintext connection password."),
        SecretInput("testDraftConnection", "admin.connections.test-draft", "honua_admin_connections_test_draft", "Accepts a plaintext connection password."),
        Session("getAdminAuthSession", "admin.auth-session.get", "honua_admin_auth_session_get", "Reads browser-session-bound authentication state."),
        Session("issueAdminOperatorBearer", "admin.auth-session.issue-bearer", "honua_admin_auth_session_issue_bearer", "Issues a bearer from browser-session authority."),
        Session("logoutAdminAuthSession", "admin.auth-session.logout", "honua_admin_auth_session_logout", "Mutates the caller's browser-bound session."),
        Session("createAdminAuthAuthorizeUrl", "admin.auth-session.authorize-url", "honua_admin_auth_session_authorize_url", "Creates a browser-session-bound authorization flow."),
        Session("requestAdminAuthToken", "admin.auth-session.request-token", "honua_admin_auth_session_request_token", "Exchanges browser authentication state for secret token material."),
        Session("getAdminAuthLogoutUrl", "admin.auth-session.logout-url", "honua_admin_auth_session_logout_url", "Creates a browser-session-bound logout flow."),
    ];

    public const string AgentSelfGovernanceReasonCode = "agent-self-governance";

    public const string AiAdmissionOperationPrefix = "admin.ai.admission.";

    // An AI agent must never change or disable its own admission policy, so those controls stay off MCP even before they are listed.
    public static IReadOnlyList<CatalogOnlyEntry> CatalogOnly { get; } =
    [
        AgentSelfGovernance("admin.ai.admission.initialize", "honua_admin_ai_admission_initialize", "Initializes the AI invocation budget an agent is admitted under."),
        AgentSelfGovernance("admin.ai.admission.policy-update", "honua_admin_ai_admission_policy_update", "Changes the AI invocation budget an agent is admitted under."),
        AgentSelfGovernance("admin.ai.admission.stop", "honua_admin_ai_admission_stop", "Engages the AI invocation kill switch."),
        AgentSelfGovernance("admin.ai.admission.resume", "honua_admin_ai_admission_resume", "Releases the AI invocation kill switch."),
    ];

    public sealed record CatalogOnlyEntry(
        string OperationId,
        string ToolName,
        string ReasonCode,
        string Explanation);

    public static string Digest { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', All.Select(entry =>
            $"{entry.OpenApiOperationId}|{entry.OperationId}|{entry.ToolName}|{entry.ReasonCode}|{entry.Explanation}")))));

    public static bool ContainsOperation(string operationId) =>
        All.Any(entry => string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)) ||
        CatalogOnly.Any(entry => string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)) ||
        operationId.StartsWith(AiAdmissionOperationPrefix, StringComparison.Ordinal);

    public static bool RequiresSecretAwareRuntime(string operationId) =>
        All.Any(entry =>
            string.Equals(entry.OperationId, operationId, StringComparison.Ordinal) &&
            (string.Equals(entry.ReasonCode, OneTimeSecretReasonCode, StringComparison.Ordinal) ||
             string.Equals(entry.ReasonCode, SecretInputReasonCode, StringComparison.Ordinal)));

    private static Entry Secret(string openApiId, string operationId, string toolName, string explanation) =>
        new(openApiId, operationId, toolName, OneTimeSecretReasonCode, explanation);

    private static Entry SecretInput(string openApiId, string operationId, string toolName, string explanation) =>
        new(openApiId, operationId, toolName, SecretInputReasonCode, explanation);

    private static CatalogOnlyEntry AgentSelfGovernance(string operationId, string toolName, string explanation) =>
        new(operationId, toolName, AgentSelfGovernanceReasonCode, explanation);

    private static Entry Session(string openApiId, string operationId, string toolName, string explanation) =>
        new(openApiId, operationId, toolName, "browser-session-bound", explanation);
}
