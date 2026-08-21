// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Security;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Keeps the generated admin MCP family reference-only for credentials. Raw credential
/// fields are removed from advertised schemas and rejected at invocation time, while
/// operations whose only useful result is a one-time secret are not published at all.
/// </summary>
internal static class AdminPublishedOperationSafety
{
    private static readonly IReadOnlyList<AdminMcpOperationExclusion> ExclusionRoster =
    [
        new("admin.api-key.create", "createAdminApiKey", "one-time-secret-result", "The endpoint returns API key material only once."),
        new("admin.api-key.rotate", "rotateAdminApiKey", "one-time-secret-result", "The endpoint returns rotated API key material only once."),
        new("admin.oauth-client.create", "registerOAuthClient", "one-time-secret-result", "The endpoint returns an OAuth client secret only once."),
        new("admin.openapi.create-embed-key", "createEmbedKey", "one-time-secret-result", "The endpoint returns embed key material only once."),
        new("admin.openapi.rotate-embed-key", "rotateEmbedKey", "one-time-secret-result", "The endpoint returns rotated embed key material only once."),
        new("admin.openapi.issue-admin-operator-bearer", "issueAdminOperatorBearer", "one-time-secret-result", "The endpoint returns a forwardable operator bearer."),
        new("admin.openapi.create-admin-auth-authorize-url", "createAdminAuthAuthorizeUrl", "session-bound-auth-flow", "The response contains one-time OIDC state and requires an HttpOnly pending-session cookie."),
        new("admin.openapi.request-admin-auth-token", "requestAdminAuthToken", "session-bound-auth-flow", "The endpoint consumes a one-time authorization code and requires an HttpOnly pending-session cookie."),
        new("admin.openapi.get-admin-auth-session", "getAdminAuthSession", "session-bound-auth-flow", "The endpoint reads the caller's authenticated browser cookie session, which the internal MCP invoker cannot supply."),
        new("admin.openapi.get-admin-auth-logout-url", "getAdminAuthLogoutUrl", "session-bound-auth-flow", "The response can contain a session-bound OIDC logout token hint."),
        new("admin.openapi.logout-admin-auth-session", "logoutAdminAuthSession", "session-bound-auth-flow", "The endpoint requires the caller's authenticated cookie session and its logout URL can contain an OIDC ID-token hint."),
    ];

    private static readonly HashSet<string> OneTimeSecretOperationIds =
        ExclusionRoster.Select(exclusion => exclusion.OperationId).ToHashSet(StringComparer.Ordinal);

    /// <summary>Deterministic exclusions consumed by roster and coverage reporting.</summary>
    public static IReadOnlyList<AdminMcpOperationExclusion> Exclusions => ExclusionRoster;

    /// <summary>The number of catalog operations intentionally withheld from MCP.</summary>
    public static int WithheldOperationCount => OneTimeSecretOperationIds.Count;

    public static bool IsPublishable(string operationId)
        => !OneTimeSecretOperationIds.Contains(operationId);

    public static OperationDescriptor SanitizeDescriptor(OperationDescriptor descriptor)
    {
        if (!descriptor.OperationId.StartsWith("admin.", StringComparison.Ordinal)
            || descriptor.InputJsonSchema is not { } inputSchema)
        {
            return descriptor;
        }

        var root = JsonNode.Parse(inputSchema.GetRawText())
            ?? throw new InvalidOperationException(
                $"Admin operation '{descriptor.OperationId}' has an invalid input schema.");
        RemoveRawCredentialProperties(root);

        using var document = JsonDocument.Parse(root.ToJsonString());
        return descriptor with { InputJsonSchema = document.RootElement.Clone() };
    }

    public static bool ContainsRawCredential(JsonElement? arguments)
        => arguments is { } value && CredentialFieldClassifier.ContainsRawCredential(value);

    private static void RemoveRawCredentialProperties(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties)
            {
                var removed = properties
                    .Where(property => CredentialFieldClassifier.IsRawCredential(property.Key))
                    .Select(property => property.Key)
                    .ToArray();
                foreach (var propertyName in removed)
                {
                    properties.Remove(propertyName);
                }

                if (obj["required"] is JsonArray required)
                {
                    for (var index = required.Count - 1; index >= 0; index--)
                    {
                        if (required[index] is JsonValue requiredValue
                            && requiredValue.TryGetValue<string>(out var requiredName)
                            && removed.Contains(requiredName, StringComparer.Ordinal))
                        {
                            required.RemoveAt(index);
                        }
                    }
                }
            }

            foreach (var child in obj.Select(property => property.Value).Where(child => child is not null))
            {
                RemoveRawCredentialProperties(child!);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child is not null))
            {
                RemoveRawCredentialProperties(child!);
            }
        }
    }

}

/// <summary>One admin catalog operation intentionally excluded from MCP publication.</summary>
internal sealed record AdminMcpOperationExclusion(
    string OperationId,
    string OpenApiOperationId,
    string Code,
    string Reason);
