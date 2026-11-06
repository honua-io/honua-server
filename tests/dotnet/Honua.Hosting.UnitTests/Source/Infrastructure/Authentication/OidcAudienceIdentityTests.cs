// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class OidcAudienceIdentityTests
{
    private const string Issuer = "https://identity.example.test";
    private const string AdditionalIssuer = "https://additional.example.test";
    private const string Audience = "Honua";
    private const string SigningKey = "audience-identity-test-signing-key-at-least-thirty-two-bytes";

    [UnitTheory]
    [InlineData("factory", "Honua", true)]
    [InlineData("factory", "honua", false)]
    [InlineData("factory-fallback", "Honua", true)]
    [InlineData("factory-fallback", "honua", false)]
    [InlineData("narrowed", "Honua", true)]
    [InlineData("narrowed", "honua", false)]
    [InlineData("bearer", "Honua", true)]
    [InlineData("bearer", "honua", false)]
    [InlineData("bearer-fallback", "Honua", true)]
    [InlineData("bearer-fallback", "honua", false)]
    public void ValidateToken_AudienceIdentity_RequiresExactMatch(string path, string audience, bool accepted)
    {
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOidcAuthentication(configuration);
        using var provider = services.BuildServiceProvider();
        var parameters = CreateParameters(path, configuration, provider);
        var issuer = path.EndsWith("fallback", StringComparison.Ordinal) ? AdditionalIssuer : Issuer;
        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(new JwtSecurityToken(
            issuer, audience, [new Claim("sub", "local-test-user")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256)));

        if (accepted)
        {
            Assert.NotNull(handler.ValidateToken(token, parameters, out _));
        }
        else
        {
            Assert.Throws<SecurityTokenInvalidAudienceException>(() => handler.ValidateToken(token, parameters, out _));
        }
    }

    private static TokenValidationParameters CreateParameters(
        string path, IConfiguration configuration, IServiceProvider services)
    {
        if (path.StartsWith("bearer", StringComparison.Ordinal))
        {
            return services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(OidcAuthenticationExtensions.JwtBearerScheme).TokenValidationParameters;
        }

        var options = new OidcAuthenticationOptions();
        OidcAuthenticationExtensions.BindOptions(configuration.GetSection("Oidc"), options);
        var runtime = Substitute.For<IOidcRuntimeProviderSource>();
        runtime.Current.Returns(Array.Empty<OidcRuntimeProvider>());
        var parameters = OidcTokenValidationParametersFactory.Build(options, runtime).Parameters;
        if (path != "narrowed")
        {
            return parameters;
        }

        var registration = OidcProviderCatalog.GetProviders(options, runtime).Single(p => p.Key == "oidc");
        return OidcTokenValidationParametersFactory.NarrowToProvider(parameters, registration, options);
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:Generic:Enabled"] = "true",
            ["Oidc:Generic:Authority"] = Issuer,
            ["Oidc:Generic:ClientId"] = Audience,
            ["Oidc:TokenValidation:SymmetricSigningKey"] = SigningKey,
            ["Oidc:TokenValidation:ValidIssuers:0"] = AdditionalIssuer,
            ["Oidc:TokenValidation:ValidAudiences:0"] = Audience
        }).Build();
}
