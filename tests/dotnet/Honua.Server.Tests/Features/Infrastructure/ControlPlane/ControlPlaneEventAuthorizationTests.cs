// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Security tests for the control-plane event endpoint authorization gate (PA-060).
/// Verifies that the endpoint fails closed (503) when no token is configured, rather
/// than the prior fail-open behaviour that admitted any caller on unconfigured deployments.
/// </summary>
public sealed class ControlPlaneEventAuthorizationTests
{
    // ---- Fail-closed when no token is configured (PA-060) -------------------

    [Fact]
    public void CheckAuthorization_NoTokenConfigured_Returns503()
    {
        var request = Request();
        var config = Configuration(); // no token entries

        var result = ControlPlaneEventEndpoints.CheckAuthorization(request, config);

        result.Should().NotBeNull("an unconfigured deployment must not be accessible");
        StatusOf(result!).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public void CheckAuthorization_TokenConfiguredViaControlPlaneKey_AndHeaderAbsent_Returns401()
    {
        var request = Request();
        var config = Configuration("ControlPlane:EventToken", "secret-token");

        var result = ControlPlaneEventEndpoints.CheckAuthorization(request, config);

        result.Should().NotBeNull();
        StatusOf(result!).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void CheckAuthorization_TokenConfiguredViaEnvVar_AndHeaderAbsent_Returns401()
    {
        var request = Request();
        var config = Configuration("HONUA_CONTROL_PLANE_EVENT_TOKEN", "secret-token");

        var result = ControlPlaneEventEndpoints.CheckAuthorization(request, config);

        result.Should().NotBeNull();
        StatusOf(result!).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void CheckAuthorization_TokenConfigured_WrongTokenInHeader_Returns403()
    {
        const string correctToken = "correct-secret";
        var request = Request(token: "wrong-token");
        var config = Configuration("ControlPlane:EventToken", correctToken);

        var result = ControlPlaneEventEndpoints.CheckAuthorization(request, config);

        result.Should().NotBeNull();
        StatusOf(result!).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void CheckAuthorization_TokenConfigured_CorrectTokenInHeader_ReturnsNull()
    {
        const string token = "the-correct-token";
        var request = Request(token: token);
        var config = Configuration("ControlPlane:EventToken", token);

        var result = ControlPlaneEventEndpoints.CheckAuthorization(request, config);

        result.Should().BeNull("a correct token must be authorized");
    }

    [Fact]
    public void CheckAuthorization_TokenConfigured_CorrectTokenAsBearer_ReturnsNull()
    {
        const string token = "the-correct-bearer-token";
        var request = Request(bearerToken: token);
        var config = Configuration("ControlPlane:EventToken", token);

        var result = ControlPlaneEventEndpoints.CheckAuthorization(request, config);

        result.Should().BeNull("Bearer authorization header must be accepted alongside the named header");
    }

    // ---- helpers -----------------------------------------------------------

    private static HttpRequest Request(string? token = null, string? bearerToken = null)
    {
        var context = new DefaultHttpContext();
        if (token is not null)
        {
            context.Request.Headers[ControlPlaneEventEndpoints.TokenHeader] = token;
        }

        if (bearerToken is not null)
        {
            context.Request.Headers["Authorization"] = $"Bearer {bearerToken}";
        }

        return context.Request;
    }

    private static IConfiguration Configuration(string? key = null, string? value = null)
    {
        var values = new Dictionary<string, string?>();
        if (key is not null)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>
    /// Extracts the HTTP status code from an <see cref="IResult"/> by executing it against a
    /// minimal <see cref="DefaultHttpContext"/> with the services needed by JSON results.
    /// </summary>
    private static int StatusOf(IResult result)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<JsonOptions>>(Options.Create(new JsonOptions()));
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        context.Response.Body = System.IO.Stream.Null;
        result.ExecuteAsync(context).GetAwaiter().GetResult();
        return context.Response.StatusCode;
    }
}
