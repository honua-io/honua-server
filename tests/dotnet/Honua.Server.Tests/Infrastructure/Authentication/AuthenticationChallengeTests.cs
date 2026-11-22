// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AuthenticationChallengeTests
{
    [UnitTest]
    public void AccessDenial_BasicCompatibilityOff_OffersApiKeyOnly()
    {
        var context = CreateContext(new() { EnableBasicAuthCompatibility = false, RequireHttpsForBasicAuth = true }, "https");

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);

        Assert.Equal([AuthenticationChallenge.ApiKeyChallenge], Challenges(context));
    }

    [UnitTest]
    public void AccessDenial_BasicCompatibilityOnOverHttps_OffersApiKeyAndBasic()
    {
        var context = CreateContext(new() { EnableBasicAuthCompatibility = true, RequireHttpsForBasicAuth = true }, "https");

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);

        Assert.Equal([AuthenticationChallenge.ApiKeyChallenge, AuthenticationChallenge.BasicChallenge], Challenges(context));
    }

    [UnitTest]
    public void AccessDenial_BasicRequiresHttpsOverHttp_DoesNotOfferBasic()
    {
        var context = CreateContext(new() { EnableBasicAuthCompatibility = true, RequireHttpsForBasicAuth = true }, "http");

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);

        Assert.Equal([AuthenticationChallenge.ApiKeyChallenge], Challenges(context));
    }

    [UnitTest]
    public void AccessDenial_BasicAllowedOverHttp_OffersBasic()
    {
        var context = CreateContext(new() { EnableBasicAuthCompatibility = true, RequireHttpsForBasicAuth = false }, "http");

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);

        Assert.Equal([AuthenticationChallenge.ApiKeyChallenge, AuthenticationChallenge.BasicChallenge], Challenges(context));
    }

    [UnitTest]
    public void AccessDenial_BearerAttempted_OffersBearerOnly()
    {
        var context = CreateContext(new() { EnableBasicAuthCompatibility = true, RequireHttpsForBasicAuth = true }, "https");
        context.Request.Headers.Authorization = "Bearer expired-token";

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);

        Assert.Equal(["Bearer"], Challenges(context));
    }

    [UnitTest]
    public void AccessDenial_NoAuthenticationOptionsRegistered_OffersApiKeyOnly()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);

        Assert.Equal([AuthenticationChallenge.ApiKeyChallenge], Challenges(context));
    }

    [UnitTest]
    public void AccessDenial_RepeatedAppends_DeduplicateCredentialChallenges()
    {
        var context = CreateContext(new() { EnableBasicAuthCompatibility = true, RequireHttpsForBasicAuth = true }, "https");

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);
        AccessPolicyHelpers.AppendAuthenticationChallenge(context);
        AuthenticationChallenge.AppendCredentialChallenges(context);

        Assert.Equal([AuthenticationChallenge.ApiKeyChallenge, AuthenticationChallenge.BasicChallenge], Challenges(context));
    }

    [UnitTest]
    public void AccessDenial_RepeatedBearerAppends_EmitSingleBearerChallenge()
    {
        var context = CreateContext(new() { EnableBasicAuthCompatibility = true, RequireHttpsForBasicAuth = true }, "https");
        context.Request.Headers.Authorization = "Bearer expired-token";

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);
        AccessPolicyHelpers.AppendAuthenticationChallenge(context);

        Assert.Equal(["Bearer"], Challenges(context));
    }

    private static DefaultHttpContext CreateContext(ApiKeyAuthenticationOptions options, string scheme)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Scheme = scheme;
        return context;
    }

    private static string[] Challenges(HttpContext context) =>
        context.Response.Headers.WWWAuthenticate.Select(static value => value ?? string.Empty).ToArray();
}
