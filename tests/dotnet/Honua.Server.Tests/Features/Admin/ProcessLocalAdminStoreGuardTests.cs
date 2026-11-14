// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ProcessLocalAdminStoreGuardTests
{
    [UnitTheory]
    [InlineData("2", HttpStatusCode.Conflict)]
    [InlineData("1", HttpStatusCode.OK)]
    public async Task Write_ProcessLocalStore_RefusedOnlyWithSeveralTasks(string maxTasks, HttpStatusCode expected)
    {
        await using var app = await StartAsync(maxTasks, services => services.AddSingleton<IOidcProviderStore>(new InMemoryOidcProviderStore()));
        using var client = app.GetTestClient();

        using var response = await client.PostAsync("/providers", content: null);

        response.StatusCode.Should().Be(expected);
        if (expected is HttpStatusCode.Conflict)
        {
            (await response.Content.ReadAsStringAsync()).Should().Contain("OIDC providers").And.Contain("more than one task");
        }
    }

    [UnitTest]
    public async Task Write_SharedStore_IsNotRefusedWithSeveralTasks()
    {
        await using var app = await StartAsync("4", services => services.AddSingleton(Substitute.For<IOidcProviderStore>()));
        using var client = app.GetTestClient();

        using var response = await client.PostAsync("/providers", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<WebApplication> StartAsync(string maxTasks, Action<IServiceCollection> registerStore)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Deployment:MaxTasks"] = maxTasks;
        builder.Services.AddProblemDetails();
        registerStore(builder.Services);
        var app = builder.Build();
        app.MapPost("/providers", () => Results.Ok())
            .RefuseWhenProcessLocalOnSeveralTasks<IOidcProviderStore, InMemoryOidcProviderStore>("OIDC providers");
        await app.StartAsync();
        return app;
    }
}

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ProcessLocalAdminStoreGuardEndpointTests
{
    private const string AdminPassword = "process-local-guard-admin-key";

    [IntegrationTheory]
    [InlineData(true, HttpStatusCode.Conflict)]
    [InlineData(false, HttpStatusCode.Created)]
    [Endpoint("POST /api/v1/admin/rate-limits")]
    [Endpoint("GET /api/v1/admin/rate-limits")]
    public async Task CreateRateLimitPolicy_DeploymentOfTwoTasks_RefusesOnlyProcessLocalWrites(bool processLocal, HttpStatusCode expected)
    {
        var fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .OverrideConfiguration(new Dictionary<string, string?> { ["Deployment:MaxTasks"] = "2" })
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
        if (processLocal)
        {
            fixture.ConfigureServices(services =>
            {
                services.RemoveAll<IRateLimitPolicyStore>();
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

            using var create = await client.PostAsJsonAsync("/api/v1/admin/rate-limits", new
            {
                name = $"tenant-cap-{Guid.NewGuid():N}",
                scope = "tenant",
                key = $"tenant-{Guid.NewGuid():N}",
                requestsPerWindow = 1000,
                windowDurationSeconds = 60d,
                enabled = true,
            });
            using var list = await client.GetAsync("/api/v1/admin/rate-limits");

            create.StatusCode.Should().Be(expected);
            if (processLocal)
            {
                (await create.Content.ReadAsStringAsync()).Should().Contain("Rate-limit policies").And.Contain("more than one task");
            }
            list.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
