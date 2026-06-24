// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Optional pluggable IdP/OIDC federation for the OAuth2 <c>client_credentials</c>
/// grant (ADR-0053 Increment 3, #1889).
/// </summary>
/// <remarks>
/// <para>
/// Operators who centralise machine identity in an external IdP can delegate the
/// <c>client_credentials</c> credential check to that IdP's token endpoint instead
/// of the in-tree client registry / Admin API-key store. This service forwards the
/// presented <c>client_id</c>/<c>client_secret</c> to the configured token endpoint
/// with <c>grant_type=client_credentials</c>; a successful exchange means the IdP
/// has authenticated the machine identity.
/// </para>
/// <para>
/// Per ADR-0049 Honua never holds a second token store: on success it discards the
/// IdP token and the caller mints a Honua portal token carrying the locally
/// configured roles. The roles are the operator's RBAC projection of "this
/// federated client is trusted"; an empty role set grants nothing (never an
/// escalation). Off by default and composes only under
/// <c>EnableClientCredentials</c>.
/// </para>
/// </remarks>
internal sealed class ClientCredentialsFederationService(
    IHttpClientFactory httpClientFactory,
    IOptions<PortalTokenAuthenticationOptions> tokenOptions)
{
    internal const string HttpClientName = "honua-client-credentials-federation";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly PortalTokenAuthenticationOptions _tokenOptions = tokenOptions.Value;

    /// <summary>Whether federation is enabled and correctly configured.</summary>
    public bool FederationEnabled
    {
        get
        {
            var options = _tokenOptions.OAuth2.ClientCredentialsFederation;
            return options.Enabled &&
                !string.IsNullOrWhiteSpace(options.TokenEndpoint) &&
                Uri.TryCreate(options.TokenEndpoint, UriKind.Absolute, out var uri) &&
                (!options.RequireHttps || uri.Scheme == Uri.UriSchemeHttps);
        }
    }

    /// <summary>
    /// Delegates a <c>client_credentials</c> exchange to the external IdP token
    /// endpoint. Returns the roles to stamp on the minted Honua token when the IdP
    /// accepts the credentials, otherwise <see langword="null"/> (the caller falls
    /// back to the in-tree client registry / API-key path, then to
    /// <c>invalid_client</c>). Never throws on an auth failure — a non-2xx response
    /// or transport error is a federation miss, not a server error.
    /// </summary>
    public async Task<IReadOnlyList<string>?> TryAuthenticateAsync(
        string? clientId,
        string clientSecret,
        string? requestedScope,
        CancellationToken cancellationToken)
    {
        if (!FederationEnabled || string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        var options = _tokenOptions.OAuth2.ClientCredentialsFederation;
        var scope = string.IsNullOrWhiteSpace(requestedScope)
            ? options.DefaultScope
            : requestedScope;

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
        };
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            form.Add(new KeyValuePair<string, string>("client_id", clientId));
        }

        form.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
        if (!string.IsNullOrWhiteSpace(scope))
        {
            form.Add(new KeyValuePair<string, string>("scope", scope!));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            // The IdP authenticated the client. We only need to confirm a token was
            // issued; the token itself is discarded (ADR-0049: no second token store).
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer
                .DeserializeAsync(stream, ClientCredentialsFederationJsonContext.Default.FederatedTokenResponse, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                return null;
            }

            return options.GrantedRoles;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout reaching the IdP — treat as a federation miss, not a 500.
            return null;
        }
    }
}

/// <summary>Minimal subset of an OAuth2 token endpoint response (RFC 6749 §5.1).</summary>
internal sealed record FederatedTokenResponse
{
    /// <summary>The issued access token (presence confirms a successful exchange).</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }
}

[JsonSerializable(typeof(FederatedTokenResponse))]
internal sealed partial class ClientCredentialsFederationJsonContext : JsonSerializerContext
{
}
