// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    private const int FailureThreshold = 3;

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    [Endpoint("GET /api/v1/admin/auth/csrf")]
    public async Task PasswordSignIn_RepeatedInvalidPasswords_RefusesSourceUntilTheBlockElapses()
    {
        var clock = new TransitionClock();
        await using var fixture = WithFailureLimiter(CreateBaseFixture(), clock, "203.0.113.40");
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        browser.BaseAddress = new Uri("https://localhost");
        await SetCsrfHeaderAsync(browser);

        for (var attempt = 0; attempt < FailureThreshold; attempt++)
        {
            using var rejected = await browser.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "invalid" });
            rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var refused = await browser.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromMinutes(15));
        refused.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await refused.Content.ReadAsStringAsync()).Should().Contain("Too many failed authentication attempts");

        clock.Advance(TimeSpan.FromMinutes(15));
        using var admitted = await browser.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        admitted.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/password-token")]
    public async Task PasswordToken_ValidPasswordBeforeThreshold_DoesNotResetTheCount()
    {
        await using var fixture = WithFailureLimiter(CreatePasswordTokenFixture(), new TransitionClock(), "203.0.113.41");
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        browser.BaseAddress = new Uri("https://localhost");
        browser.DefaultRequestHeaders.Add("Origin", "https://console.example.com");

        // Five requests stay inside the route's own per-minute rate limit.
        var attempts = new[]
        {
            ("invalid", HttpStatusCode.Unauthorized), ("invalid", HttpStatusCode.Unauthorized), ("test-admin-key", HttpStatusCode.OK),
            ("invalid", HttpStatusCode.Unauthorized), ("test-admin-key", HttpStatusCode.TooManyRequests),
        };
        foreach (var (password, status) in attempts)
        {
            using var response = await browser.PostAsJsonAsync("/api/v1/admin/auth/password-token", new { password });
            response.StatusCode.Should().Be(status);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_RepeatedWrongPasswords_RefusesTheCorrectPassword()
    {
        await using var fixture = WithFailureLimiter(CreateBaseFixture(), new TransitionClock(), "203.0.113.43");
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        client.BaseAddress = new Uri("https://localhost");
        (await GenerateTokenAsync(client, "test-admin-key")).Should().Contain("\"token\"");

        for (var attempt = 0; attempt < FailureThreshold; attempt++)
        {
            (await GenerateTokenAsync(client, "invalid")).Should().NotContain("\"token\"");
        }

        (await GenerateTokenAsync(client, "test-admin-key")).Should().NotContain("\"token\"");
    }

    private static async Task<string> GenerateTokenAsync(HttpClient client, string password)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = password,
            ["client"] = "requestip",
            ["f"] = "json",
        });
        using var response = await client.PostAsync("/sharing/rest/generateToken", content);
        return await response.Content.ReadAsStringAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/password-token")]
    public async Task PasswordToken_RepeatedInvalidPasswords_RefusesWithRetryAfter()
    {
        await using var fixture = WithFailureLimiter(CreatePasswordTokenFixture(), new TransitionClock(), "203.0.113.42");
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        browser.BaseAddress = new Uri("https://localhost");
        browser.DefaultRequestHeaders.Add("Origin", "https://console.example.com");

        for (var attempt = 0; attempt < FailureThreshold; attempt++)
        {
            using var rejected = await browser.PostAsJsonAsync("/api/v1/admin/auth/password-token", new { password = "invalid" });
            rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var refused = await browser.PostAsJsonAsync("/api/v1/admin/auth/password-token", new { password = "test-admin-key" });
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
    }

    // Each test uses its own address so the route's per-address rate limit cannot carry over between tests.
    private static WebAppFixture WithFailureLimiter(WebAppFixture fixture, TransitionClock clock, string source)
    {
        var limiter = new AuthenticationFailureLimiter(
            Options.Create(new AuthenticationFailureLimiterOptions { Threshold = FailureThreshold }),
            clock,
            NullLogger<AuthenticationFailureLimiter>.Instance);
        return fixture
            .ReplaceService<IAuthenticationFailureLimiter>(limiter)
            .ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(IPAddress.Parse(source))));
    }

    // The in-process test transport leaves RemoteIpAddress empty; Kestrel always sets it.
    private sealed class RemoteAddressStartupFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                await nextMiddleware(context).ConfigureAwait(false);
            });
            next(app);
        };
    }
}
