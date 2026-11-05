// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

// Do not call this for a secret a local store already accepted; on success the IdP token is discarded and an empty role set grants nothing.
internal sealed class ClientCredentialsFederationService(
    IHttpClientFactory httpClientFactory,
    IOptions<PortalTokenAuthenticationOptions> tokenOptions)
{
    internal const string HttpClientName = "honua-client-credentials-federation";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly PortalTokenAuthenticationOptions _tokenOptions = tokenOptions.Value;

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

    // An authentication failure never throws: a non-success, a non-JSON body, or a transport error is a federation miss.
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
        catch (JsonException)
        {
            // A 2xx body that is not a token document is a federation miss (#6166).
            return null;
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

internal sealed record FederatedTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }
}

[JsonSerializable(typeof(FederatedTokenResponse))]
internal sealed partial class ClientCredentialsFederationJsonContext : JsonSerializerContext
{
}
