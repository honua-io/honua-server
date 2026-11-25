// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Backpressure;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.RateLimiting;
using Honua.Server.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Certification;

public sealed partial class SdkHardeningParityContractTests
{
    private static IResult CreateStandardErrorResult(HttpContext context, int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => StandardErrorHelpers.CreateBadRequest(context, "Invalid request parameter."),
        StatusCodes.Status401Unauthorized => StandardErrorHelpers.CreateUnauthorized(context, "Authentication required."),
        StatusCodes.Status403Forbidden => StandardErrorHelpers.CreateForbidden(context, "Permission denied."),
        StatusCodes.Status404NotFound => StandardErrorHelpers.CreateNotFound(context, "Layer not found."),
        StatusCodes.Status409Conflict => StandardErrorHelpers.CreateConflict(context, "Resource already exists."),
        StatusCodes.Status429TooManyRequests => StandardErrorHelpers.CreateTooManyRequests(
            context,
            "Quota exceeded.",
            retryAfterSeconds: 15),
        StatusCodes.Status503ServiceUnavailable => StandardErrorHelpers.CreateServiceUnavailable(
            context,
            "Service temporarily unavailable.",
            retryAfterSeconds: 30),
        _ => throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Unsupported SDK contract status code."),
    };

    private static void AssertStatusSpecificHeaders(HttpContext context, JsonElement root, int statusCode)
    {
        if (statusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
        {
            context.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
            return;
        }

        if (statusCode is StatusCodes.Status429TooManyRequests or StatusCodes.Status503ServiceUnavailable)
        {
            var expectedCode = statusCode == StatusCodes.Status429TooManyRequests
                ? BackpressureMetadata.RateLimitExceededCode
                : BackpressureMetadata.ServiceUnavailableCode;
            root.GetProperty("code").GetString().Should().Be(expectedCode);
            root.GetProperty("retryable").GetBoolean().Should().BeTrue();
            root.GetProperty("retryAfterSeconds").GetInt32().Should().BeGreaterThan(0);
            context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
            context.Response.Headers["X-Correlation-ID"].ToString().Should().NotBeNullOrWhiteSpace();
            int.TryParse(
                context.Response.Headers.RetryAfter.ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var retryAfterSeconds).Should().BeTrue();
            retryAfterSeconds.Should().BeGreaterThan(0);
        }
    }

    private static Exception CreateThrownException(int expectedStatus)
    {
        var sensitiveMessage = $"Failure at {SensitiveInternalPath}; Password={SensitiveSecret}";
        Exception candidate = expectedStatus switch
        {
            StatusCodes.Status400BadRequest => new ArgumentException(sensitiveMessage),
            StatusCodes.Status401Unauthorized => new UnauthorizedAccessException(sensitiveMessage),
            StatusCodes.Status403Forbidden => new ResourceAccessDeniedException(),
            StatusCodes.Status404NotFound => new ResourceNotFoundException(sensitiveMessage),
            StatusCodes.Status409Conflict => new ResourceConflictException(sensitiveMessage),
            StatusCodes.Status503ServiceUnavailable => new ServiceUnavailableException(sensitiveMessage, retryAfterSeconds: 30),
            _ => new InvalidOperationException(sensitiveMessage),
        };
        try
        {
            throw candidate;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void AssertNoInternalLeakage(string payload)
    {
        payload.Should().NotContain(SensitiveSecret);
        payload.Should().NotContain(SensitiveInternalPath);
        payload.Should().NotContain(".cs:line");
        payload.Should().NotContain("StackTrace");
        payload.Should().NotContain("at Honua.");
        payload.Should().NotContain("Debug:");
    }

    private static RateLimitingMiddleware CreateRateLimitingMiddleware(int limit)
    {
        var policyStore = Substitute.For<IRateLimitPolicyStore>();
        return new RateLimitingMiddleware(
            next: static context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            policyStore,
            Options.Create(new RateLimitingOptions
            {
                Enabled = true,
                GlobalRequestsPerMinute = limit,
            }),
            NullLogger<RateLimitingMiddleware>.Instance);
    }

    private static TestHttpScope CreateHttpScope(string path, string? remoteIp = null, string? subject = null)
    {
        var parsedRemoteIp = string.IsNullOrWhiteSpace(remoteIp) ? null : IPAddress.Parse(remoteIp);
        ClaimsPrincipal? principal = null;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            Claim[] claims = [new Claim("sub", subject)];
            principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
        }

        return new TestHttpScope(path, parsedRemoteIp, principal);
    }

    private static string ReadResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static async Task<ForwardedProbeResponse> SendForwardedRequestAsync(ForwardedProbeRequest probe)
    {
        await using var app = await StartForwardedProbeAppAsync(probe.PeerAddress);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features/collections");
        request.Headers.Add("X-Forwarded-Proto", probe.ForwardedProto);
        request.Headers.Add("X-Forwarded-Host", probe.ForwardedHost);
        request.Headers.Add("X-Forwarded-For", probe.ForwardedFor);

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var parts = (await response.Content.ReadAsStringAsync()).Split('|');
        return new ForwardedProbeResponse
        {
            Scheme = parts[0],
            Host = parts[1],
            RemoteAddress = parts[2],
            AbsoluteUrl = parts[3],
            ResolvedBaseUrl = parts[4],
        };
    }

    private static async Task<WebApplication> StartForwardedProbeAppAsync(string peerAddress)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:ForwardLimit"] = "1",
            ["ForwardedHeaders:KnownNetworks:0"] = TrustedProxyNetwork,
        });
        StartupConfigurationHelpers.ConfigureForwardedHeaders(builder.Services, builder.Configuration).Should().BeTrue();

        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peerAddress);
            return next(context);
        });
        app.UseForwardedHeaders();
        app.MapGet("/ogc/features/collections", static (HttpContext context) =>
        {
            var absoluteUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}";
            var resolvedBaseUrl = BaseUrlResolver.GetBaseUrl(context);
            return $"{context.Request.Scheme}|{context.Request.Host}|{context.Connection.RemoteIpAddress}|{absoluteUrl}|{resolvedBaseUrl}";
        });
        await app.StartAsync();
        return app;
    }

    private sealed record ForwardedProbeRequest
    {
        public required string PeerAddress { get; init; }

        public required string ForwardedProto { get; init; }

        public required string ForwardedHost { get; init; }

        public required string ForwardedFor { get; init; }
    }

    private sealed record ForwardedProbeResponse
    {
        public required string Scheme { get; init; }

        public required string Host { get; init; }

        public required string RemoteAddress { get; init; }

        public required string AbsoluteUrl { get; init; }

        public required string ResolvedBaseUrl { get; init; }
    }

    private sealed class TestHttpScope : IDisposable
    {
        private readonly MemoryStream _body = new();
        private readonly ServiceProvider _services = BuildServices();

        public TestHttpScope(string path, IPAddress? remoteIp, ClaimsPrincipal? principal)
        {
            Context = new DefaultHttpContext
            {
                RequestServices = _services,
            };
            Context.Request.Method = HttpMethods.Get;
            Context.Request.Path = path;
            Context.Request.Scheme = Uri.UriSchemeHttps;
            Context.Request.Host = new HostString("api.example.org");
            Context.Response.Body = _body;
            Context.Connection.RemoteIpAddress = remoteIp;
            if (principal is not null)
            {
                Context.User = principal;
            }
        }

        public DefaultHttpContext Context { get; }

        public void Dispose()
        {
            _body.Dispose();
            _services.Dispose();
        }

        private static ServiceProvider BuildServices()
        {
            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.TenantId.Returns("tenant-sdk-parity");
            tenantContext.Source.Returns(TenantContextSource.Claim);
            return new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .AddSingleton(tenantContext)
                .BuildServiceProvider();
        }
    }
}
