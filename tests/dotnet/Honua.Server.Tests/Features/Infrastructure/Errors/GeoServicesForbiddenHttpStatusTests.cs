// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class GeoServicesForbiddenHttpStatusTests
{
    [UnitTheory]
    [InlineData(false, 200)]
    [InlineData(true, 403)]
    public async Task Forbidden_UsesRealStatusOnlyWithEndpointOptIn(bool optedIn, int expectedStatus)
    {
        var context = CreateContext(optedIn);
        await StandardErrorHelpers.CreateForbidden(context, "Write permission required").ExecuteAsync(context);

        await AssertEnvelopeAsync(context, expectedStatus, 403);
        context.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        BypassOutputCacheOnErrorEnvelopePolicy.TryGetErrorEnvelopeCode(context, out var code).Should().BeTrue();
        code.Should().Be(403);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingToken_WithOrWithoutOptIn_Retains200And499(bool optedIn)
    {
        var context = CreateContext(optedIn);
        await StandardErrorHelpers.CreateUnauthorized(context, "Authentication required").ExecuteAsync(context);
        await AssertEnvelopeAsync(context, 200, 499);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidToken_WithOrWithoutOptIn_Retains200And498(bool optedIn)
    {
        var context = CreateContext(optedIn);
        await StandardErrorHelpers.CreateInvalidToken(context).ExecuteAsync(context);
        await AssertEnvelopeAsync(context, 200, 498);
    }

    [UnitTheory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task OtherErrors_EndpointOptInDoesNotChangeEnvelopePolicy(int status)
    {
        var context = CreateContext(true);
        var error = new StandardErrorResponse(status, "Failure", "Original detail");
        await StandardErrorResponseFormatter.FormatError(context, error).ExecuteAsync(context);
        await AssertEnvelopeAsync(context, 200, status);
    }

    [UnitTest]
    public async Task ExplicitBodyTokenCode_IsNotConvertedToForbiddenTransport()
    {
        var context = CreateContext(true);
        var error = StandardErrorResponse.Forbidden("Invalid token");
        await StandardErrorResponseFormatter.FormatError(context, error,
            new ErrorResponseFormatterOptions { GeoServicesBodyCode = 498 }).ExecuteAsync(context);
        await AssertEnvelopeAsync(context, 200, 498);
    }

    private static DefaultHttpContext CreateContext(bool optedIn)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/rest/services/test/FeatureServer/0/updateFeatures";
        context.Request.Method = HttpMethods.Post;
        context.RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        if (optedIn)
        {
            context.SetEndpoint(new Endpoint(null,
                new EndpointMetadataCollection(GeoServicesForbiddenHttpStatusMetadata.Instance), "Edit"));
        }
        return context;
    }

    private static async Task AssertEnvelopeAsync(HttpContext context, int status, int code)
    {
        context.Response.StatusCode.Should().Be(status);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(code);
        body.RootElement.TryGetProperty("updateResults", out _).Should().BeFalse();
    }
}
