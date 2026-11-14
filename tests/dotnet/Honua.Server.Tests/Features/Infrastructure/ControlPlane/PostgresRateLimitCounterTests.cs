// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Infrastructure.RateLimiting;
using Honua.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresRateLimitCounterTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task AdmitAsync_ConcurrentIndependentNodes_ShareOneBudget()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresRateLimitCounterTests));
        var generation = await harness.CreateConfigurationDocuments().GetGenerationAsync(CancellationToken.None);
        var nodes = new[] { harness.CreateRateCounterStore(), harness.CreateRateCounterStore() };
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            nodes[index % 2].AdmitAsync("private-subject", new ResolvedRateLimit(7, TimeSpan.FromDays(1), string.Empty), CancellationToken.None)));

        results.Count(result => result.IsAllowed).Should().Be(7);
        (await harness.ScalarAsync<long>($"SELECT request_count FROM {harness.Table("rate_limit_counters")}"))
            .Should().Be(40);
        // A restarted task reads the same consumed budget, without any process-local cache.
        (await harness.CreateRateCounterStore().AdmitAsync("private-subject", new ResolvedRateLimit(7, TimeSpan.FromDays(1), string.Empty), CancellationToken.None))
            .IsAllowed.Should().BeFalse();
        (await harness.ScalarAsync<string>($"SELECT counter_key FROM {harness.Table("rate_limit_counters")}"))
            .Should().NotContain("private-subject");
        (await harness.CreateConfigurationDocuments().GetGenerationAsync(CancellationToken.None))
            .Should().Be(generation, "request admission must not invalidate configuration caches");
    }

    [IntegrationTest]
    public async Task AdmitAsync_ExpiredWindow_ResetsBudget()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresRateLimitCounterTests));
        var node = harness.CreateRateCounterStore();
        (await node.AdmitAsync("expired", new ResolvedRateLimit(1, TimeSpan.FromMinutes(1), string.Empty), CancellationToken.None)).IsAllowed.Should().BeTrue();
        (await node.AdmitAsync("expired", new ResolvedRateLimit(1, TimeSpan.FromMinutes(1), string.Empty), CancellationToken.None)).IsAllowed.Should().BeFalse();
        await harness.ExecuteAsync($"UPDATE {harness.Table("rate_limit_counters")} SET window_reset = CURRENT_TIMESTAMP - interval '1 minute'");

        var result = await harness.CreateRateCounterStore().AdmitAsync("expired", new ResolvedRateLimit(1, TimeSpan.FromMinutes(1), string.Empty), CancellationToken.None);
        result.IsAllowed.Should().BeTrue();
        result.RequestsRemaining.Should().Be(0);
        result.WindowReset.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [IntegrationTest]
    public async Task Pipelines_RestartAndLowerEndpointCap_PreserveSharedPolicyBudget()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresRateLimitCounterTests));
        await harness.CreateRateStore().CreatePolicyAsync(new RateLimitPolicy
        {
            PolicyId = Guid.NewGuid(), Name = "Shared plan", Scope = RateLimitScopes.Plan,
            Key = "test-plan", RequestsPerWindow = 4, WindowDuration = TimeSpan.FromDays(1)
        });
        await using var taskA = await CreatePipelineAsync(harness);
        await using var taskB = await CreatePipelineAsync(harness);
        var clientA = taskA.GetTestClient();
        var clientB = taskB.GetTestClient();

        (await clientA.GetAsync("/map")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await clientB.GetAsync("/map")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await clientB.GetAsync("/other")).StatusCode.Should().Be(HttpStatusCode.OK);
        await taskA.StopAsync();
        await using var restartedTask = await CreatePipelineAsync(harness);
        var restarted = restartedTask.GetTestClient();
        (await restarted.GetAsync("/other")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await restarted.GetAsync("/other")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("rate_limit_counters")}"))
            .Should().Be(2, "the subject and lower endpoint budgets must both be stored in PostgreSQL");
    }

    [IntegrationTheory]
    [InlineData("rate_limit_counters")]
    [InlineData("configuration_documents")]
    public async Task Pipeline_SharedStoreUnavailable_Returns503WithoutAdmission(string table)
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresRateLimitCounterTests));
        await using var app = await CreatePipelineAsync(harness);
        await harness.ExecuteAsync($"DROP TABLE {harness.Table(table)}");

        var response = await app.GetTestClient().GetAsync("/other");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    [IntegrationTest]
    public async Task AdmitAsync_LowerPolicyCap_DoesNotResetConsumedBudget()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresRateLimitCounterTests));
        var node = harness.CreateRateCounterStore();
        (await node.AdmitAsync("subject", new ResolvedRateLimit(10, TimeSpan.FromDays(1), string.Empty), CancellationToken.None)).IsAllowed.Should().BeTrue();
        (await node.AdmitAsync("subject", new ResolvedRateLimit(10, TimeSpan.FromDays(1), string.Empty), CancellationToken.None)).IsAllowed.Should().BeTrue();

        var result = await harness.CreateRateCounterStore().AdmitAsync("subject", new ResolvedRateLimit(2, TimeSpan.FromDays(1), string.Empty), CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.RequestCount.Should().Be(3);
        result.Limit.Should().Be(2);
    }

    private static async Task<WebApplication> CreatePipelineAsync(ControlPlaneStateHarness harness)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IRateLimitPolicyStore>(harness.CreateRateStore());
        builder.Services.AddSingleton(harness.CreateRateCounterStore());
        builder.Services.Configure<RateLimitingOptions>(options =>
        {
            options.Enabled = true;
            options.GlobalRequestsPerMinute = 1000;
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.206");
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("sub", "shared-subject"), new Claim("plan", "test-plan")
            }, "test"));
            await next();
        });
        app.UseRateLimiting();
        app.MapGet("/map", () => Results.Ok()).WithMetadata(new RateLimitAttribute(1));
        app.MapGet("/other", () => Results.Ok());
        await app.StartAsync();
        return app;
    }
}
