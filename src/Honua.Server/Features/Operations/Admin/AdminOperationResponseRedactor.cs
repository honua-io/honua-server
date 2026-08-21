// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Operations.Admin;

/// <summary>
/// Redacts raw credential values from in-process Admin API responses before they
/// cross the published-operation/MCP result boundary.
/// </summary>
internal static class AdminOperationResponseRedactor
{
    private const string RedactedValue = "[REDACTED]";

    public static string Redact(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return response;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(response);
        }
        catch (JsonException)
        {
            // The Admin API is JSON. If an endpoint violates that contract, its opaque
            // body cannot be proven credential-safe and therefore must not be surfaced.
            return RedactedValue;
        }

        if (root is null)
        {
            return response;
        }

        RedactNode(root);
        return root.ToJsonString();
    }

    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (CredentialFieldClassifier.IsRawCredential(property.Key))
                {
                    obj[property.Key] = RedactedValue;
                }
                else if (property.Value is JsonValue value
                         && value.TryGetValue<string>(out var text)
                         && TryRemoveBearerUrlComponents(text, out var sanitized))
                {
                    obj[property.Key] = sanitized;
                }
                else if (property.Value is not null)
                {
                    RedactNode(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child is not null))
            {
                RedactNode(child!);
            }
        }
    }

    private static bool TryRemoveBearerUrlComponents(string? value, out string sanitized)
    {
        sanitized = value ?? string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || (string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        sanitized = builder.Uri.AbsoluteUri;
        return true;
    }
}
