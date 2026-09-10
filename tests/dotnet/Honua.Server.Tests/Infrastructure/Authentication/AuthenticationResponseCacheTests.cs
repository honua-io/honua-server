// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[Protocol(TestProtocols.TestQuality)]
public sealed class AuthenticationResponseCachePolicyTests
{
    private static DefaultHttpContext CreateAuthenticatedContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"));
        return context;
    }

    [UnitTest]
    public void Apply_AuthenticatedResponse_WithExplicitPrivateCacheControl_PreservesEndpointPolicy()
    {
        var context = CreateAuthenticatedContext();
        context.Response.Headers.CacheControl = "private, max-age=3600";
        context.Response.Headers.Vary = "Authorization";

        AuthenticationResponseCachePolicy.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=3600");
    }

    [UnitTest]
    public void Apply_AuthenticatedResponse_WithPublicCacheControl_ForcesNoStore()
    {
        var context = CreateAuthenticatedContext();
        context.Response.Headers.CacheControl = "public, max-age=3600";

        AuthenticationResponseCachePolicy.Apply(context);

        var cacheControl = context.Response.GetTypedHeaders().CacheControl;
        cacheControl.Should().NotBeNull();
        cacheControl!.NoStore.Should().BeTrue();
    }

    [UnitTest]
    public void Apply_AuthenticatedResponse_WithNoCacheControl_ForcesNoStore()
    {
        var context = CreateAuthenticatedContext();

        AuthenticationResponseCachePolicy.Apply(context);

        var cacheControl = context.Response.GetTypedHeaders().CacheControl;
        cacheControl.Should().NotBeNull();
        cacheControl!.NoStore.Should().BeTrue();
    }

    [UnitTest]
    public void Apply_UnauthorizedStatus_ForcesNoStore_EvenWithExplicitPrivateCacheControl()
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "private, max-age=3600";

        AuthenticationResponseCachePolicy.Apply(context);

        var cacheControl = context.Response.GetTypedHeaders().CacheControl;
        cacheControl.Should().NotBeNull();
        cacheControl!.NoStore.Should().BeTrue();
    }
}

[Protocol(TestProtocols.TestQuality)]
public sealed class AuthenticationErrorCacheTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", Tiers.Fast)]
    [InlineData("/rest/services/alpha/FeatureServer/0/query", 401)]
    [InlineData("/rest/services/alpha/FeatureServer/0/query", 403)]
    [InlineData("/ogc/features/collections/0/items", 401)]
    [InlineData("/ogc/features/collections/0/items", 403)]
    [InlineData("/odata/Layers(0)/Features", 401)]
    [InlineData("/odata/Layers(0)/Features", 403)]
    [InlineData("/wms", 401)]
    [InlineData("/wms", 403)]
    [InlineData("/wmts", 401)]
    [InlineData("/wmts", 403)]
    [InlineData("/wcs", 401)]
    [InlineData("/wcs", 403)]
    [InlineData("/wfs", 401)]
    [InlineData("/wfs", 403)]
    public void FormatError_AuthenticationDecision_DoesNotPermitStorage(string path, int logicalStatus)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        // A formatter option must not make an authentication decision reusable.
        var options = new ErrorResponseFormatterOptions
        {
            AdditionalHeaders = new Dictionary<string, string> { ["Cache-Control"] = "public, max-age=600" }
        };

        StandardErrorResponseFormatter.FormatError(context,
            new StandardErrorResponse(logicalStatus, "Denied", "Authentication decision"), options);

        var cacheControl = context.Response.GetTypedHeaders().CacheControl;
        cacheControl.Should().NotBeNull();
        cacheControl!.NoStore.Should().BeTrue();
    }

    [UnitTest]
    public void InvalidToken_GeoServicesHttp200_DoesNotPermitStorage()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/rest/services/alpha/FeatureServer/0/query";

        var result = StandardErrorHelpers.CreateInvalidToken(context);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        ((ApiErrorResponse)((IValueHttpResult)result).Value!).Error.Code.Should().Be(498);
        var cacheControl = context.Response.GetTypedHeaders().CacheControl;
        cacheControl.Should().NotBeNull();
        cacheControl!.NoStore.Should().BeTrue();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", Tiers.Fast)]
    [InlineData(400)]
    [InlineData(404)]
    public void FormatError_NonAuthenticationDecision_PreservesExplicitCachePolicy(int status)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/rest/services/alpha/FeatureServer/0/query";
        context.Response.Headers.CacheControl = "public, max-age=600";

        StandardErrorResponseFormatter.FormatError(context, new StandardErrorResponse(status, "Error", "Control"));

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=600");
    }
}

[Protocol(TestProtocols.FeatureServer)]
public sealed class AuthenticationResponseCacheTests
{
    [IntegrationTest]
    [Operation(Operations.GetLayerInfo)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task PublicMetadata_Anonymous_DoesNotDisableStorage()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(alphaServiceMetadata:
                ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymous: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/alpha/FeatureServer/0?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        body.RootElement.GetProperty("id").GetInt32().Should().Be(0);
        (response.Headers.CacheControl?.NoStore ?? false).Should().BeFalse();
    }

    [IntegrationTheory]
    [Operation(Operations.GetLayerInfo)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [InlineData("anonymous", 499)]
    [InlineData("reader", 0)]
    [InlineData("outsider", 403)]
    public async Task ProtectedMetadata_CredentialState_DoesNotPermitStorage(string role, int expectedError)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(alphaServiceMetadata:
                ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["reader"])));
        using var client = role == "anonymous" ? factory.CreateClient() : ServiceRbacTestFixture.CreateClient(factory, role);

        using var response = await client.GetAsync("/rest/services/alpha/FeatureServer/0?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (expectedError == 0)
        {
            body.RootElement.TryGetProperty("error", out _).Should().BeFalse();
            body.RootElement.GetProperty("id").GetInt32().Should().Be(0);
        }
        else
        {
            body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(expectedError);
        }

        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }
}

/// <summary>
/// Blocked-license denial for a credential-response endpoint must still carry
/// <c>Cache-Control: no-store</c> (#4609 review): the deny path short-circuits before the
/// endpoint runs, so the cache middleware's OnStarting registration must happen ahead of
/// the license middleware in the real pipeline, not just when a test wires it that way.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class LicenseBlockedCredentialResponseCacheTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ReplaceService<ILicenseOperationPolicy>(new AlwaysBlockedLicenseOperationPolicy());

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/generateToken")]
    public async Task GenerateToken_LicenseBlocked_DoesNotPermitStorage()
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync("/sharing/rest/generateToken");

        // GeoServices formats the license denial as a logical error on an HTTP 200
        // envelope rather than a literal 402; the no-store guarantee must hold either way.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":402");
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private sealed class AlwaysBlockedLicenseOperationPolicy : ILicenseOperationPolicy
    {
        public bool IsBlocked => true;

        public CancellationToken OperationCancellation => CancellationToken.None;
    }
}
