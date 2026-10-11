// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Identity.Domain;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// The portal credential verifier uses catalog order and the selected provider's
/// issuer and audience. A token from another configured authority is not accepted
/// for a broker flow that started at a different provider.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class OidcAuthoritySelectionTests
{
    private const string SigningKey = "ThisIsATestSigningKeyThatIsLongEnoughForHS256Algorithm!";
    private const string GoogleIssuer = "https://accounts.google.com";
    private const string GoogleAudience = "google-client";
    private const string OktaIssuer = "https://dev-12345.okta.com/oauth2/default";
    private const string OktaAudience = "okta-client";

    [UnitTest]
    public void Build_UsesCatalogAuthorityOrder_IncludingRuntimeProviders()
    {
        var registry = new OidcRuntimeProviderRegistry();
        try
        {
            registry.Replace(
            [
                new OidcRuntimeProvider(
                    "runtime-1",
                    "Generic",
                    "Runtime",
                    "https://runtime.example.com",
                    "runtime-client",
                    Enabled: true),
            ]);
            var built = OidcTokenValidationParametersFactory.Build(AllProviders(), registry);

            Assert.Equal(
                [
                    "https://login.microsoftonline.com/contoso/v2.0",
                    GoogleIssuer,
                    OktaIssuer,
                    "https://tenant.us.auth0.com",
                    "https://idp.example.com/generic",
                    "https://runtime.example.com",
                ],
                built.Authorities);
        }
        finally
        {
            OidcProviderCatalog.Attach(null);
        }
    }

    [UnitTest]
    public async Task VerifyForProvider_RejectsATokenFromAnotherConfiguredAuthority()
    {
        var verifier = CreateVerifier();
        var oktaToken = CreateToken(OktaIssuer, OktaAudience);
        var googleToken = CreateToken(GoogleIssuer, GoogleAudience);
        var crossed = CreateToken(GoogleIssuer, OktaAudience);

        var selectedOkta = await verifier.VerifyForProviderAsync("okta", oktaToken, CancellationToken.None);
        var anyProvider = await verifier.VerifyAsync("user", oktaToken, CancellationToken.None);
        var substituted = await verifier.VerifyForProviderAsync("google", oktaToken, CancellationToken.None);
        var google = await verifier.VerifyForProviderAsync("google", googleToken, CancellationToken.None);
        var wrongAudience = await verifier.VerifyAsync("user", crossed, CancellationToken.None);

        Assert.NotNull(selectedOkta);
        Assert.NotNull(anyProvider);
        Assert.NotNull(google);
        Assert.Null(substituted);
        Assert.Null(wrongAudience);
    }

    [UnitTest]
    public async Task CreateProvider_PublishesClientSecretOntoTheCatalogRegistration()
    {
        var registry = new OidcRuntimeProviderRegistry();
        try
        {
            var store = new InMemoryOidcProviderStore(registry: registry);
            var providerId = Guid.NewGuid();
            const string secret = " confidential-secret ";
            await store.CreateProviderAsync(
                new OidcProviderConfiguration
                {
                    ProviderId = providerId,
                    Name = "Confidential",
                    ProviderType = "Generic",
                    Authority = "https://idp.example.com/confidential",
                    ClientId = "confidential-client",
                    ClientSecret = secret,
                    Enabled = true,
                },
                CancellationToken.None);

            var resolved = OidcProviderCatalog.TryResolveProvider(
                new OidcAuthenticationOptions { Enabled = true },
                providerId.ToString("D"),
                out var registration,
                registry);

            Assert.True(resolved);
            Assert.Equal(secret, registration.ClientSecret);
            Assert.Equal("confidential-client", registration.ClientId);
        }
        finally
        {
            OidcProviderCatalog.Attach(null);
        }
    }

    [UnitTest]
    public async Task VerifyAsync_RejectsATokenSignedByAnotherProvidersKey()
    {
        using var directory = new LocalOidcDirectory();
        var registry = new OidcRuntimeProviderRegistry();
        try
        {
            registry.Replace(
            [
                new OidcRuntimeProvider(
                    "provider-a", "Generic", "A", directory.AuthorityA, "client-a", Enabled: true),
                new OidcRuntimeProvider(
                    "provider-b", "Generic", "B", directory.AuthorityB, "client-b", Enabled: true),
            ]);
            var verifier = CreateDiscoveryVerifier(registry);
            var matching = CreateRsaToken(directory.AuthorityA, "client-a", directory.RsaA, "key-a");
            var crossed = CreateRsaToken(directory.AuthorityA, "client-a", directory.RsaB, "key-b");
            var other = CreateRsaToken(directory.AuthorityB, "client-b", directory.RsaB, "key-b");

            var accepted = await verifier.VerifyAsync("user", matching, CancellationToken.None);
            var rejected = await verifier.VerifyAsync("user", crossed, CancellationToken.None);
            var otherAccepted = await verifier.VerifyAsync("user", other, CancellationToken.None);

            Assert.NotNull(accepted);
            Assert.Equal("user-1", accepted!.PrincipalId);
            Assert.Null(rejected);
            Assert.NotNull(otherAccepted);
            Assert.Equal("user-1", otherAccepted!.PrincipalId);
        }
        finally
        {
            OidcProviderCatalog.Attach(null);
        }
    }

    private static OidcPortalCredentialVerifier CreateVerifier()
    {
        var options = AllProviders();
        options.TokenValidation.SymmetricSigningKey = SigningKey;
        var wrapped = Options.Create(options);
        var transformation = new OidcClaimsTransformation(
            wrapped,
            NullLogger<OidcClaimsTransformation>.Instance,
            new ServiceCollection().BuildServiceProvider());
        return new OidcPortalCredentialVerifier(
            wrapped,
            transformation,
            new OidcConfigurationManagerCache(),
            NullLogger<OidcPortalCredentialVerifier>.Instance);
    }

    private static OidcAuthenticationOptions AllProviders() => new()
    {
        Enabled = true,
        RequireHttps = false,
        AzureAd = new AzureAdProviderOptions
        {
            Enabled = true,
            TenantId = "contoso",
            ClientId = "azure-client",
            ClientSecret = "secret",
        },
        Google = new GoogleProviderOptions
        {
            Enabled = true,
            ClientId = GoogleAudience,
            ClientSecret = "secret",
        },
        Okta = new OktaProviderOptions
        {
            Enabled = true,
            OrgUrl = "dev-12345.okta.com",
            ClientId = OktaAudience,
            ClientSecret = "secret",
        },
        Auth0 = new Auth0ProviderOptions
        {
            Enabled = true,
            Domain = "tenant.us.auth0.com",
            ClientId = "auth0-client",
            ClientSecret = "secret",
            Audience = "https://api.example.com",
        },
        Generic = new GenericOidcProviderOptions
        {
            Enabled = true,
            Authority = "https://idp.example.com/generic",
            ClientId = "generic-client",
        },
    };

    private static string CreateToken(string issuer, string audience)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: [new Claim("sub", "user-1"), new Claim("name", "User One")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler { MapInboundClaims = false }.WriteToken(token);
    }

    private static OidcPortalCredentialVerifier CreateDiscoveryVerifier(IOidcRuntimeProviderSource runtime)
    {
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            RequireHttps = false,
        };
        var wrapped = Options.Create(options);
        var transformation = new OidcClaimsTransformation(
            wrapped,
            NullLogger<OidcClaimsTransformation>.Instance,
            new ServiceCollection().BuildServiceProvider());
        return new OidcPortalCredentialVerifier(
            wrapped,
            transformation,
            new OidcConfigurationManagerCache(),
            NullLogger<OidcPortalCredentialVerifier>.Instance,
            runtime);
    }

    private static string CreateRsaToken(string issuer, string audience, RSA rsa, string kid)
    {
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "user-1"),
                new Claim("name", "User One"),
            ]),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(rsa) { KeyId = kid },
                SecurityAlgorithms.RsaSha256),
        });
    }

    private sealed class LocalOidcDirectory : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _jwksA;
        private readonly string _jwksB;

        public LocalOidcDirectory()
        {
            RsaA = RSA.Create(2048);
            RsaB = RSA.Create(2048);
            _jwksA = ToJwks(RsaA, "key-a");
            _jwksB = ToJwks(RsaB, "key-b");

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var prefix = $"http://127.0.0.1:{port}/";
            AuthorityA = prefix + "a";
            AuthorityB = prefix + "b";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string AuthorityA { get; }

        public string AuthorityB { get; }

        public RSA RsaA { get; }

        public RSA RsaB { get; }

        public void Dispose()
        {
            _listener.Close();
            RsaA.Dispose();
            RsaB.Dispose();
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                var body = path.Equals("/a/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase)
                    ? Discovery(AuthorityA, AuthorityA + "/jwks")
                    : path.Equals("/b/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase)
                        ? Discovery(AuthorityB, AuthorityB + "/jwks")
                        : path.Equals("/a/jwks", StringComparison.OrdinalIgnoreCase)
                            ? _jwksA
                            : path.Equals("/b/jwks", StringComparison.OrdinalIgnoreCase)
                                ? _jwksB
                                : null;
                var buffer = Encoding.UTF8.GetBytes(body ?? string.Empty);
                context.Response.StatusCode = body is null ? 404 : 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                try
                {
                    await context.Response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
                    context.Response.Close();
                }
                catch (HttpListenerException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private static string Discovery(string issuer, string jwksUri)
            => $$"""
                {"issuer":"{{issuer}}","jwks_uri":"{{jwksUri}}","authorization_endpoint":"{{issuer}}/authorize","token_endpoint":"{{issuer}}/token","response_types_supported":["code"],"subject_types_supported":["public"],"id_token_signing_alg_values_supported":["RS256"]}
                """;

        private static string ToJwks(RSA rsa, string kid)
        {
            var parameters = rsa.ExportParameters(false);
            var modulus = Base64UrlEncoder.Encode(parameters.Modulus);
            var exponent = Base64UrlEncoder.Encode(parameters.Exponent);
            return $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"{{kid}}","alg":"RS256","n":"{{modulus}}","e":"{{exponent}}"}]}""";
        }
    }
}
