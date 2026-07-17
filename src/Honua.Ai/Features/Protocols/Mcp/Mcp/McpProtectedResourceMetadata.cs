// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// OAuth 2.0 Protected Resource Metadata document (RFC 9728 section 2) for the
/// <c>/mcp</c> resource. Member names are the wire names the RFC defines, so every
/// property carries an explicit <see cref="JsonPropertyNameAttribute"/> rather than
/// relying on a naming policy.
/// </summary>
/// <remarks>
/// Only the members Honua can honestly answer for are modelled. <c>bearer_methods_supported</c>
/// advertises <c>header</c> now that the surface accepts <c>Authorization: Bearer</c> tokens
/// as a resource server (honua-server#2850). <c>scopes_supported</c> remains deliberately
/// absent while the surface authenticates but does not yet enforce OAuth scopes
/// (honua-server#2851) — advertising a scope vocabulary the runtime does not enforce is the
/// advertised-vs-actual gap #2803 exists to close.
/// </remarks>
internal sealed class McpProtectedResourceMetadataDocument
{
    /// <summary>
    /// The protected resource's resource identifier (RFC 9728 <c>resource</c>, REQUIRED).
    /// </summary>
    [JsonPropertyName("resource")]
    public required string Resource { get; init; }

    /// <summary>
    /// Issuer identifiers of the authorization servers that can issue tokens for this
    /// resource (RFC 9728 <c>authorization_servers</c>).
    /// </summary>
    [JsonPropertyName("authorization_servers")]
    public required IReadOnlyList<string> AuthorizationServers { get; init; }

    /// <summary>
    /// The bearer-token presentation methods the resource accepts (RFC 9728
    /// <c>bearer_methods_supported</c>, values from RFC 6750). The <c>/mcp</c> surface reads
    /// the token from the <c>Authorization</c> request header only, so the sole advertised
    /// method is <c>header</c>.
    /// </summary>
    [JsonPropertyName("bearer_methods_supported")]
    public IReadOnlyList<string>? BearerMethodsSupported { get; init; }

    /// <summary>
    /// Human-readable name of the protected resource (RFC 9728 <c>resource_name</c>).
    /// </summary>
    [JsonPropertyName("resource_name")]
    public string? ResourceName { get; init; }
}

/// <summary>
/// AOT-compatible JSON serialization context for the RFC 9728 metadata document.
/// </summary>
[JsonSerializable(typeof(McpProtectedResourceMetadataDocument))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class McpProtectedResourceMetadataJsonContext : JsonSerializerContext;

/// <summary>
/// Derives the RFC 9728 protected-resource metadata for <c>/mcp</c> from the OIDC
/// authority configuration that already drives authentication
/// (<see cref="OidcAuthenticationOptions"/> via <see cref="OidcProviderCatalog"/>), so the
/// advertised authorization servers cannot drift from the ones the host actually trusts.
/// </summary>
internal static class McpProtectedResourceMetadata
{
    /// <summary>
    /// Well-known URI path suffix registered by RFC 9728 section 3.
    /// </summary>
    private const string WellKnownPrefix = "/.well-known/oauth-protected-resource";

    /// <summary>
    /// Route the metadata document is served from. RFC 9728 section 3 inserts the
    /// well-known path <em>between the host component and the path</em> of the resource
    /// identifier, so a resource at <c>/mcp</c> publishes at
    /// <c>/.well-known/oauth-protected-resource/mcp</c> — not at <c>/mcp/.well-known/...</c>.
    /// </summary>
    public const string RoutePath = WellKnownPrefix + McpEndpointExtensions.RoutePath;

    /// <summary>
    /// Human-readable resource name advertised in the metadata document.
    /// </summary>
    public const string ResourceName = "Honua MCP";

    /// <summary>
    /// The RFC 6750 bearer-token presentation methods advertised in
    /// <c>bearer_methods_supported</c>. The <c>/mcp</c> surface accepts the token only from
    /// the <c>Authorization</c> request header (honua-server#2850).
    /// </summary>
    public static readonly IReadOnlyList<string> BearerMethodsSupported = ["header"];

    /// <summary>
    /// Resolves the issuer identifiers of every configured, valid OIDC provider. Returns an
    /// empty list when OIDC is disabled or no provider is fully configured, which is the
    /// signal that no metadata document should exist at all.
    /// </summary>
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

    /// <summary>
    /// Builds the <c>/mcp</c> resource identifier from the host's public base URL. Uses the
    /// shared <see cref="BaseUrlResolver"/> so the identifier matches every other link the
    /// server emits and never trusts an unvalidated Host header.
    /// </summary>
    public static bool TryResolveResourceIdentifier(HttpContext context, out Uri resource)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        return Uri.TryCreate(baseUrl + McpEndpointExtensions.RoutePath, UriKind.Absolute, out resource!);
    }

    /// <summary>
    /// Constructs the metadata URL for a resource identifier per RFC 9728 section 3:
    /// the well-known path is inserted between the host component and the resource's path,
    /// with any terminating slash after the host removed first.
    /// </summary>
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
