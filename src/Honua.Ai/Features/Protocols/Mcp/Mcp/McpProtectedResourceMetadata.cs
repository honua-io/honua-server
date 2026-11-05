// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;

namespace Honua.Ai.Protocols.Mcp;

internal sealed class McpProtectedResourceMetadataDocument
{
    [JsonPropertyName("resource")]
    public required string Resource { get; init; }

    [JsonPropertyName("authorization_servers")]
    public required IReadOnlyList<string> AuthorizationServers { get; init; }

    [JsonPropertyName("bearer_methods_supported")]
    public IReadOnlyList<string>? BearerMethodsSupported { get; init; }

    [JsonPropertyName("scopes_supported")]
    public IReadOnlyList<string>? ScopesSupported { get; init; }

    [JsonPropertyName("resource_name")]
    public string? ResourceName { get; init; }
}

[JsonSerializable(typeof(McpProtectedResourceMetadataDocument))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class McpProtectedResourceMetadataJsonContext : JsonSerializerContext;

internal static class McpProtectedResourceMetadata
{
    private const string WellKnownPrefix = "/.well-known/oauth-protected-resource";

    // RFC 9728 inserts the well-known path between the host and the resource path, not under /mcp.
    public const string RoutePath = WellKnownPrefix + McpEndpointExtensions.RoutePath;

    public const string ResourceName = "Honua MCP";

    public static readonly IReadOnlyList<string> BearerMethodsSupported = ["header"];

    public static readonly IReadOnlyList<string> ScopesSupported = OperatorScopeCatalog.SupportedScopes;

    public static IReadOnlyList<string> ResolveAuthorizationServers(OidcAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return [];
        }

        return OidcProviderCatalog.GetProviders(options)
            .Where(provider => provider.IsValid && !string.IsNullOrWhiteSpace(provider.Authority))
            .Select(provider => provider.Authority!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static bool TryResolveResourceIdentifier(HttpContext context, out Uri resource)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        return Uri.TryCreate(baseUrl + McpEndpointExtensions.RoutePath, UriKind.Absolute, out resource!);
    }

    public static string BuildMetadataUrl(Uri resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var path = resource.AbsolutePath;
        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            path = string.Empty;
        }

        return resource.GetLeftPart(UriPartial.Authority) + WellKnownPrefix + path;
    }
}
