// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class SharedCacheGuardTests
{
    [UnitTheory]
    [InlineData(nameof(ResponseCacheClass.Tile))]
    [InlineData(nameof(ResponseCacheClass.FeatureQuery))]
    [InlineData(nameof(ResponseCacheClass.StaticMapImage))]
    [InlineData(nameof(ResponseCacheClass.StaticObject))]
    public void Apply_AccessNeverDecided_IsNotShared(string cacheClassName)
    {
        var context = SharedCacheTestContexts.AnonymousGet(Enum.Parse<ResponseCacheClass>(cacheClassName));

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTheory]
    [InlineData(nameof(ResponseCacheClass.Tile))]
    [InlineData(nameof(ResponseCacheClass.MetadataDocument))]
    public void Apply_AccessDenied_IsNotSharedEvenWhenAnEarlierLayerWasGranted(string cacheClassName)
    {
        var context = SharedCacheTestContexts.AnonymousGet(Enum.Parse<ResponseCacheClass>(cacheClassName));
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        ResponseAccessVerdict.Record(context, AccessDecision.RequiresAuth());

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Record_AuthenticatedCallerGranted_IsRestricted()
    {
        var context = SharedCacheTestContexts.AnonymousGet(ResponseCacheClass.Tile);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "editor")], "Bearer"));

        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());

        ResponseAccessVerdict.Restricted(context).Should().BeTrue();
        ResponseAccessVerdict.GrantedToAnonymous(context).Should().BeFalse();
    }

    [UnitTest]
    public void Apply_HandlerForbadeStorage_KeepsTheRefusal()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        context.Response.Headers.CacheControl = "no-store";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTheory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public void Guard_ErrorStatus_IsNeverShared(int statusCode)
    {
        var context = SharedCacheTestContexts.GrantedTile();
        SharedCacheResponse.Apply(context);
        context.Response.StatusCode = statusCode;

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_NoCacheControlAtAll_WritesTheSafeDefault()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers.ETag = "\"abc\"";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_RefusalThatAlsoCarriesALifetime_LeavesAsExactlyOneRefusal()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        context.Response.Headers.CacheControl = "no-store, max-age=3600, private";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_GeoServicesRefusalSentAs200WithNoCacheControl_IsNotShared()
    {
        var context = SharedCacheTestContexts.AnonymousGet(ResponseCacheClass.Tile);
        ResponseAccessVerdict.Record(context, AccessDecision.RequiresAuth());
        BypassOutputCacheOnErrorEnvelopePolicy.MarkErrorEnvelope(context, 499);
        context.Response.StatusCode = StatusCodes.Status200OK;

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_ErrorEnvelopeOnAnOtherwisePublicResponse_IsNotShared()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        SharedCacheResponse.Apply(context);
        BypassOutputCacheOnErrorEnvelopePolicy.MarkErrorEnvelope(context, 500);

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_ReplayedResponseStatesAnAge_TheAgeIsRemoved()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        SharedCacheResponse.Apply(context);
        context.Response.Headers.Age = "288";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.ContainsKey("Age").Should().BeFalse();
        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [UnitTest]
    public void Guard_ResponseSetsACookie_IsNotShared()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        SharedCacheResponse.Apply(context);
        context.Response.Headers.SetCookie = "session=1";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTheory]
    [InlineData("X-Honua-Job-Id")]
    [InlineData("X-API-Key")]
    public void Apply_RequestCarriesACredentialHeader_WritesNothingPublic(string header)
    {
        var context = SharedCacheTestContexts.GrantedTile();
        context.Request.Headers[header] = "credential";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Apply_ForwardedClientCertificateUnderARenamedHeader_IsNotShared()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:ClientCertificates:ForwardedCertificate:HeaderName"] = "X-Edge-Client-Cert",
                })
                .Build())
            .BuildServiceProvider();
        context.Request.Headers["X-Edge-Client-Cert"] = "credential";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Apply_MutualTlsConnection_IsNotShared()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        context.Connection.ClientCertificate = certificate;

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Apply_EdgeMarkedTheRequestAsCacheBypass_IsNotShared()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        context.Request.Headers.Cookie = "honua_cache_bypass=1";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }
}
