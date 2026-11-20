// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Encodings.Web;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

// A Prometheus Operator ServiceMonitor can send only an Authorization header, so the
// API-key handler also reads "Authorization: ApiKey <key>".
public sealed class ApiKeyAuthorizationSchemeTests
{
    private const string BootstrapAdminPassword = "AuthorizationScheme-AdminPassword123!";

    [UnitTheory]
    [InlineData("ApiKey")]
    [InlineData("apikey")]
    [InlineData("APIKEY")]
    public async Task AuthorizationApiKey_ValidScopedKey_Authenticates(string scheme)
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("prometheus-scrape", ["admin:read"], null, "test", CancellationToken.None);
        var context = ContextWithAuthorization($"{scheme} {created.Key}");

        var result = await AuthenticateAsync(store, context);

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public async Task AuthorizationApiKey_BootstrapAdminKey_Authenticates()
    {
        var context = ContextWithAuthorization($"ApiKey {BootstrapAdminPassword}");

        var result = await AuthenticateAsync(new InMemoryAdminApiKeyStore(), context);

        result.Succeeded.Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("ApiKey wrong-key-value")]
    [InlineData("ApiKey ")]
    [InlineData("ApiKey    ")]
    [InlineData("ApiKeywrong-key-value")]
    public async Task AuthorizationApiKey_InvalidOrEmptyKey_IsRefused(string authorization)
    {
        var context = ContextWithAuthorization(authorization);

        var result = await AuthenticateAsync(new InMemoryAdminApiKeyStore(), context);

        result.Succeeded.Should().BeFalse();
    }

    [UnitTest]
    public async Task AuthorizationBearer_ValidKey_IsNotReadAsApiKey()
    {
        var context = ContextWithAuthorization($"Bearer {BootstrapAdminPassword}");

        var result = await AuthenticateAsync(new InMemoryAdminApiKeyStore(), context);

        result.Succeeded.Should().BeFalse("Bearer belongs to the operator bearer and OIDC schemes");
    }

    [UnitTest]
    public async Task XApiKeyHeader_WinsOverAuthorizationApiKey()
    {
        var context = ContextWithAuthorization("ApiKey wrong-key-value");
        context.Request.Headers["X-API-Key"] = BootstrapAdminPassword;

        var result = await AuthenticateAsync(new InMemoryAdminApiKeyStore(), context);

        result.Succeeded.Should().BeTrue("an explicit X-API-Key keeps precedence");
    }

    [UnitTest]
    public async Task AuthorizationApiKey_RestrictedKey_KeepsItsNetworkRestriction()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("restricted", ["admin:read"], null, "test",
            new AdminApiKeyRestrictions(null, ["203.0.113.0/24"]), CancellationToken.None);
        var allowed = ContextWithAuthorization($"ApiKey {created.Key}", "203.0.113.9");
        var refused = ContextWithAuthorization($"ApiKey {created.Key}", "198.51.100.1");

        (await AuthenticateAsync(store, allowed)).Succeeded.Should().BeTrue();
        (await AuthenticateAsync(store, refused)).Succeeded.Should().BeFalse();
    }

    private static DefaultHttpContext ContextWithAuthorization(string authorization, string clientIp = "198.51.100.1")
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorization;
        context.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
        return context;
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(IAdminApiKeyStore store, HttpContext context)
    {
        var dependencies = new ApiKeyAuthenticationDependencies(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                EnvironmentName = "Production",
                AdminPassword = BootstrapAdminPassword,
            }),
            secretResolver: null,
            adminApiKeyStore: store);
        var handler = new ApiKeyAuthenticationHandler(Schemes(), NullLoggerFactory.Instance, UrlEncoder.Default, dependencies);
        await handler.InitializeAsync(
            new AuthenticationScheme(AuthenticationExtensions.ApiKeyScheme, null, typeof(ApiKeyAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private static IOptionsMonitor<AuthenticationSchemeOptions> Schemes()
    {
        var schemes = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemes.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        return schemes;
    }
}
