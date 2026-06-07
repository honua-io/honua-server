// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.Admin.OperateFixtures;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration coverage for the Development/Test Console Operate fixture seed endpoint (#1209).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class OperateObservabilityFixtureEndpointsTests : IAsyncLifetime
{
    private const string Profile = "console-testcontainers-v1";
    private const string SeedRoute = "/api/v1/admin/dev/fixtures/operate-observability/console-testcontainers-v1";
    private const string ServiceId = "operate-fixture-console";
    private const string CorrelationId = "corr-operate-fixture-001";
    private const string SucceededJobId = "operate-fixture-job-succeeded";
    private const string FailedJobId = "operate-fixture-job-failed";
    private const string InvestigationId = "inv-operate-fixture-console";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public OperateObservabilityFixtureEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("OperateObservabilityFixture:Enabled", "true");
                builder.UseSetting("OperateObservabilityFixture:Profile", Profile);
                builder.UseSetting("OperateObservabilityFixture:SeedOnStartup", "false");
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/dev/fixtures/operate-observability/{profile}")]
    public async Task SeedOperateObservabilityFixture_WithEnabledProfile_HydratesLiveAdminSurfaces()
    {
        var seedResponse = await _client.PostAsync(SeedRoute, content: null);

        var seedBody = await seedResponse.Content.ReadAsStringAsync();
        seedResponse.StatusCode.Should().Be(HttpStatusCode.OK, seedBody);
        using var seed = JsonDocument.Parse(seedBody);
        seed.RootElement.GetProperty("profile").GetString().Should().Be(Profile);
        seed.RootElement.GetProperty("sourceHydrated").GetBoolean().Should().BeTrue();
        seed.RootElement.GetProperty("serviceId").GetString().Should().Be(ServiceId);
        seed.RootElement.GetProperty("correlationId").GetString().Should().Be(CorrelationId);
        var openAlertId = seed.RootElement.GetProperty("alerts").GetProperty("openAlertEventId").GetInt64();
        var resolvedAlertId = seed.RootElement.GetProperty("alerts").GetProperty("resolvedAlertEventId").GetInt64();

        var repeatSeed = await _client.PostAsync(SeedRoute, content: null);
        repeatSeed.StatusCode.Should().Be(HttpStatusCode.OK);

        AssertSingletonJobConsumersResolve();
        await AssertAlertZonesAsync();
        await AssertAlertRulesAsync();
        await AssertAlertEventsAsync(openAlertId, resolvedAlertId);
        await AssertJobsAsync();
        await AssertJobPaginationAsync();
        await AssertJobDefinitionAndResourceFiltersAsync();
        await AssertJobDetailsLogsAndArtifactsAsync();
        await AssertOperateEventsAsync();
        await AssertRecentLogsAsync();
        await AssertInvestigationAsync(openAlertId);
    }

    private async Task AssertAlertZonesAsync()
    {
        var response = await _client.GetAsync("/api/v1/admin/alerts/zones?serviceId=operate-fixture-console");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var zones = doc.RootElement.GetProperty("data");
        zones.EnumerateArray()
            .Should()
            .Contain(zone => zone.GetProperty("zoneName").GetString() == "Honolulu Harbor Testcontainers");
    }

    private async Task AssertAlertRulesAsync()
    {
        var response = await _client.GetAsync("/api/v1/admin/alerts/rules?serviceId=operate-fixture-console&layerId=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var rules = doc.RootElement.GetProperty("data");
        rules.EnumerateArray()
            .Should()
            .Contain(rule => rule.GetProperty("ruleName").GetString() == "Harbor Entry Testcontainers");
    }

    private async Task AssertAlertEventsAsync(long openAlertId, long resolvedAlertId)
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/alerts?serviceId=operate-fixture-console&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var alerts = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        alerts.Should().Contain(alert =>
            alert.GetProperty("eventId").GetInt64() == openAlertId &&
            alert.GetProperty("lifecycleStatus").GetString() == "open");
        alerts.Should().Contain(alert =>
            alert.GetProperty("eventId").GetInt64() == resolvedAlertId &&
            alert.GetProperty("lifecycleStatus").GetString() == "resolved");
        alerts.Count(alert => alert.GetProperty("serviceId").GetString() == ServiceId).Should().Be(2);
    }

    private async Task AssertJobsAsync()
    {
        var response = await _client.GetAsync("/api/v1/admin/jobs?correlationId=corr-operate-fixture-001&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var jobs = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        jobs.Should().Contain(job => job.GetProperty("jobId").GetString() == SucceededJobId);
        jobs.Should().Contain(job => job.GetProperty("jobId").GetString() == FailedJobId);
        jobs.Should().HaveCount(2);
    }

    private void AssertSingletonJobConsumersResolve()
    {
        _fixture.Services.GetRequiredService<IExecutionAdmissionEvaluator>()
            .Should()
            .NotBeNull();
        _fixture.Services.GetRequiredService<IGeoprocessingJobService>()
            .Should()
            .NotBeNull();
    }

    private async Task AssertJobPaginationAsync()
    {
        // limit=1 keyset pagination must page through both fixture jobs without re-including or
        // skipping the boundary row, proving the cursor is encoded from the created_at column
        // used by ORDER BY rather than the deserialized record.
        var firstResponse = await _client.GetAsync(
            "/api/v1/admin/jobs?correlationId=corr-operate-fixture-001&limit=1");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var firstPage = await ReadJsonAsync(firstResponse);
        var firstItems = firstPage.RootElement.GetProperty("items").EnumerateArray().ToArray();
        firstItems.Should().ContainSingle();
        var firstJobId = firstItems[0].GetProperty("jobId").GetString();
        var cursor = firstPage.RootElement.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrWhiteSpace();

        var secondResponse = await _client.GetAsync(
            $"/api/v1/admin/jobs?correlationId=corr-operate-fixture-001&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var secondPage = await ReadJsonAsync(secondResponse);
        var secondItems = secondPage.RootElement.GetProperty("items").EnumerateArray().ToArray();
        secondItems.Should().ContainSingle();
        var secondJobId = secondItems[0].GetProperty("jobId").GetString();

        secondJobId.Should().NotBe(firstJobId);
        new[] { firstJobId, secondJobId }.Should().BeEquivalentTo(new[] { SucceededJobId, FailedJobId });
    }

    private async Task AssertJobDefinitionAndResourceFiltersAsync()
    {
        // Mirror RedisExecutionJobStore matching: definitionId resolves against both the
        // DefinitionId and GeoprocessingPlanId parameters, and resource refs match
        // case-insensitively.
        var byPlanId = await _client.GetAsync(
            "/api/v1/admin/jobs?definitionId=operate-observability-fixture-plan&limit=10");
        byPlanId.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(byPlanId))
        {
            var jobs = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            jobs.Should().Contain(job => job.GetProperty("jobId").GetString() == SucceededJobId);
            jobs.Should().Contain(job => job.GetProperty("jobId").GetString() == FailedJobId);
        }

        var byMixedCaseResourceRef = await _client.GetAsync(
            "/api/v1/admin/jobs?resourceRef=OPERATE-FIXTURE-CONSOLE&limit=10");
        byMixedCaseResourceRef.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(byMixedCaseResourceRef))
        {
            var jobs = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            jobs.Should().Contain(job => job.GetProperty("jobId").GetString() == SucceededJobId);
            jobs.Should().Contain(job => job.GetProperty("jobId").GetString() == FailedJobId);
        }
    }

    private async Task AssertJobDetailsLogsAndArtifactsAsync()
    {
        var detail = await _client.GetAsync("/api/v1/admin/jobs/operate-fixture-job-failed");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(detail))
        {
            doc.RootElement.GetProperty("jobId").GetString().Should().Be(FailedJobId);
            doc.RootElement.GetProperty("status").GetString().Should().Be("Failed");
            doc.RootElement.GetProperty("failure").GetProperty("message").GetString()
                .Should().Contain("Synthetic artifact publication failed");
        }

        var logs = await _client.GetAsync("/api/v1/admin/jobs/operate-fixture-job-failed/logs?limit=10");
        logs.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(logs))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            items.Should().Contain(log => log.GetProperty("level").GetString() == "Error");
            items.Should().HaveCount(3);
        }

        var artifacts = await _client.GetAsync("/api/v1/admin/jobs/operate-fixture-job-failed/artifacts?limit=10");
        artifacts.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = await ReadJsonAsync(artifacts))
        {
            var item = doc.RootElement.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
            item.GetProperty("availability").GetString().Should().Be("Available");
            item.GetProperty("providerLink").GetString().Should().Contain("failed-diagnostics.json");
        }
    }

    private async Task AssertOperateEventsAsync()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/events?correlationId=corr-operate-fixture-001&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var kinds = doc.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("kind").GetString())
            .ToArray();
        kinds.Should().Contain("audit");
        kinds.Should().Contain("job");
    }

    private async Task AssertRecentLogsAsync()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/logs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        var logs = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        logs.Should().Contain(log =>
            log.GetProperty("correlationId").GetString() == CorrelationId &&
            log.GetProperty("message").GetString()!.Contains("Fixture durable job failed", StringComparison.Ordinal));
    }

    private async Task AssertInvestigationAsync(long openAlertId)
    {
        var response = await _client.GetAsync("/api/v1/admin/investigations/inv-operate-fixture-console");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        doc.RootElement.GetProperty("investigationId").GetString().Should().Be(InvestigationId);
        doc.RootElement.GetProperty("pins").EnumerateArray()
            .Should()
            .Contain(pin => pin.GetProperty("eventRef").GetString() == "alert:" + openAlertId.ToString(CultureInfo.InvariantCulture));
        doc.RootElement.GetProperty("links").EnumerateArray()
            .Should()
            .Contain(link => link.GetProperty("resourceId").GetString() == FailedJobId);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

public sealed class OperateObservabilityFixtureOptionsTests
{
    [Fact]
    public void OptionsValidator_WhenEnabledOutsideDevelopmentOrTest_Fails()
    {
        var validator = new OperateObservabilityFixtureOptionsValidator(
            new FakeHostEnvironment("Production"),
            new ConfigurationBuilder().Build());

        var result = validator.Validate(null, new OperateObservabilityFixtureOptions
        {
            Enabled = true,
            Profile = "console-testcontainers-v1"
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("Development or Test", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveEnabled_WhenFlatEnvAliasIsTrue_EnablesFixture()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_ENABLE_OBSERVABILITY_TEST_SEED"] = "true"
            })
            .Build();

        OperateObservabilityFixtureOptions.ResolveEnabled(configuration, boundEnabled: false)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void ResolveEnabled_WithNoFlagSet_StaysDisabled()
    {
        var configuration = new ConfigurationBuilder().Build();

        OperateObservabilityFixtureOptions.ResolveEnabled(configuration, boundEnabled: false)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ResolveEnabled_WhenNestedKeyIsTrue_StaysEnabledRegardlessOfAlias()
    {
        var configuration = new ConfigurationBuilder().Build();

        OperateObservabilityFixtureOptions.ResolveEnabled(configuration, boundEnabled: true)
            .Should()
            .BeTrue();
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Honua.Server.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// Verifies the fixture seed endpoint stays opt-in by default (#1209).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class OperateObservabilityFixtureDisabledEndpointTests : IAsyncLifetime
{
    private const string SeedRoute = "/api/v1/admin/dev/fixtures/operate-observability/console-testcontainers-v1";
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/dev/fixtures/operate-observability/{profile}")]
    public async Task SeedOperateObservabilityFixture_DefaultConfiguration_IsNotMapped()
    {
        var response = await _client.PostAsync(SeedRoute, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Verifies the flat <c>HONUA_ENABLE_OBSERVABILITY_TEST_SEED</c> env-var alias enables the seed
/// endpoint and produces the same deterministic alert + job records Console asserts (#1229).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class OperateObservabilityFixtureEnvAliasEndpointTests : IAsyncLifetime
{
    private const string SeedRoute = "/api/v1/admin/dev/fixtures/operate-observability/console-testcontainers-v1";
    private const string SucceededJobId = "operate-fixture-job-succeeded";
    private const string FailedJobId = "operate-fixture-job-failed";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public OperateObservabilityFixtureEnvAliasEndpointTests()
    {
        // Enable only via the flat HONUA_ENABLE_OBSERVABILITY_TEST_SEED alias; the nested
        // OperateObservabilityFixture:Enabled key is intentionally left unset.
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.UseSetting("HONUA_ENABLE_OBSERVABILITY_TEST_SEED", "true"));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/dev/fixtures/operate-observability/{profile}")]
    public async Task SeedOperateObservabilityFixture_EnabledViaFlatEnvAlias_SeedsAlertsAndJobs()
    {
        var seedResponse = await _client.PostAsync(SeedRoute, content: null);

        var seedBody = await seedResponse.Content.ReadAsStringAsync();
        seedResponse.StatusCode.Should().Be(HttpStatusCode.OK, seedBody);
        using var seed = JsonDocument.Parse(seedBody);
        var openAlertId = seed.RootElement.GetProperty("alerts").GetProperty("openAlertEventId").GetInt64();

        var alerts = await _client.GetAsync("/api/v1/admin/observability/alerts?serviceId=operate-fixture-console&pageSize=20");
        alerts.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await alerts.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("items").EnumerateArray()
                .Should()
                .Contain(alert =>
                    alert.GetProperty("eventId").GetInt64() == openAlertId &&
                    alert.GetProperty("lifecycleStatus").GetString() == "open");
        }

        var jobs = await _client.GetAsync("/api/v1/admin/jobs?correlationId=corr-operate-fixture-001&limit=10");
        jobs.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await jobs.Content.ReadAsStringAsync()))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            items.Should().Contain(job => job.GetProperty("jobId").GetString() == SucceededJobId);
            items.Should().Contain(job => job.GetProperty("jobId").GetString() == FailedJobId);
        }
    }
}
