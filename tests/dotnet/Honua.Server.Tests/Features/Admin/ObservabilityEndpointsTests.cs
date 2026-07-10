// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Core.Configuration;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ObservabilityEndpointsTests : IAsyncLifetime
{
    private readonly StubDatabaseMigrationRunner _migrationRunner = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ObservabilityEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(_migrationRunner);
                services.RemoveAll<IOptions<MigrationSafetyOptions>>();
                services.AddSingleton(Options.Create(new MigrationSafetyOptions
                {
                    BackupCommand = "pg_dump --format=custom"
                }));
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/migrations")]
    public async Task GetMigrationStatus_ReturnsInstanceScopedLifecycleAndPlan()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts:
            [
                "0001_create_honua_schema.sql",
                "0002_create_service_tables.sql"
            ]);

        _fixture.Services.GetRequiredService<MigrationState>()
            .MarkRunning("Applying 2 pending migration script(s).");

        var response = await _client.GetAsync("/api/v1/admin/observability/migrations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("running");
        root.GetProperty("isReady").GetBoolean().Should().BeFalse();
        root.GetProperty("isFailed").GetBoolean().Should().BeFalse();
        root.GetProperty("planAvailable").GetBoolean().Should().BeTrue();
        root.GetProperty("upgradeRequired").GetBoolean().Should().BeTrue();
        root.GetProperty("pendingScripts").GetArrayLength().Should().Be(2);
        root.GetProperty("message").GetString().Should().Be("Applying 2 pending migration script(s).");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/migrations")]
    public async Task GetMigrationStatus_WhenBackupHookMatchesPendingSet_ReturnsOutcome()
    {
        const string ContractScript = "002_drop_legacy_annotated.sql";
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts: [ContractScript],
            pendingScriptClassifications:
            [
                new MigrationScriptClassification
                {
                    ScriptName = ContractScript,
                    Classification = MigrationSafetyClassification.ContractAnnotated,
                    BreakingRules = ["drop-column"]
                }
            ],
            journalIsNonEmpty: true);

        _fixture.Services.GetRequiredService<MigrationBackupHookState>()
            .Record(new DatabaseMigrationBackupHookResult
            {
                Outcome = "failed",
                Succeeded = false,
                StartedAt = new DateTimeOffset(2026, 7, 9, 10, 15, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 7, 9, 10, 15, 3, TimeSpan.Zero),
                DurationMilliseconds = 3_000,
                ExitCode = 2,
                Stderr = "pg_dump: permission denied",
                PendingContractScripts = [ContractScript],
                MigrationRunId = "schema-migration-test",
                CorrelationId = "schema-migration-test"
            });

        var response = await _client.GetAsync("/api/v1/admin/observability/migrations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var backupHook = document.RootElement.GetProperty("backupHook");
        backupHook.GetProperty("configured").GetBoolean().Should().BeTrue();
        backupHook.GetProperty("requiredForPendingSet").GetBoolean().Should().BeTrue();
        backupHook.GetProperty("ranForPendingSet").GetBoolean().Should().BeTrue();
        backupHook.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        backupHook.GetProperty("outcome").GetString().Should().Be("failed");
        backupHook.GetProperty("durationMilliseconds").GetInt64().Should().Be(3_000);
        backupHook.GetProperty("exitCode").GetInt32().Should().Be(2);
        backupHook.GetProperty("stderr").GetString().Should().Be("pg_dump: permission denied");
        backupHook.GetProperty("pendingContractScripts")[0].GetString().Should().Be(ContractScript);
        backupHook.GetProperty("migrationRunId").GetString().Should().Be("schema-migration-test");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/migrations")]
    public async Task GetMigrationStatus_WhenPlanFails_ReturnsPlanErrorWithoutThrowing()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Failed(new InvalidOperationException("Unable to inspect migrations."));

        _fixture.Services.GetRequiredService<MigrationState>()
            .MarkSucceeded("No pending migration scripts.");

        var response = await _client.GetAsync("/api/v1/admin/observability/migrations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("succeeded");
        root.GetProperty("planAvailable").GetBoolean().Should().BeFalse();
        root.GetProperty("upgradeRequired").GetBoolean().Should().BeFalse();
        root.GetProperty("planError").GetString().Should().Be("Migration planning is temporarily unavailable.");
        root.GetProperty("pendingScripts").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/migrations")]
    public async Task GetMigrationStatus_WhenPlanThrows_ReturnsProblemDetails()
    {
        _migrationRunner.PlanException = new InvalidOperationException("Migration planner exploded.");

        _fixture.Services.GetRequiredService<MigrationState>()
            .MarkSucceeded("No pending migration scripts.");

        var response = await _client.GetAsync("/api/v1/admin/observability/migrations");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Internal Server Error");
        content.Should().NotContain("Migration planner exploded.");
        content.Should().NotContain("planAvailable");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/errors")]
    public async Task GetRecentErrors_WhenEmpty_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/errors");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("capacity").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("errors").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task GetTelemetryStatus_WhenOtlpNotConfigured_ReturnsRealtimeCapabilityAndNormalState()
    {
        var fixture = CreateTelemetryFixture(new Dictionary<string, string?>
        {
            ["Tracing:Enabled"] = "true",
            ["Tracing:OtlpEndpoint"] = "",
            ["Tracing:OtlpHeaders"] = "",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = ""
        });

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var response = await client.GetAsync("/api/v1/admin/observability/telemetry");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            var realtime = root.GetProperty("realtime");
            realtime.GetProperty("supported").GetBoolean().Should().BeTrue();
            realtime.GetProperty("hubPath").GetString().Should().Be("/hubs/admin");
            realtime.GetProperty("protocol").GetString().Should().Be("signalr");
            realtime.GetProperty("events").EnumerateArray()
                .Should()
                .Contain(element => element.GetString() == "AdminStatusChanged");

            root.GetProperty("tracingEnabled").GetBoolean().Should().BeTrue();
            root.GetProperty("metricsEnabled").GetBoolean().Should().BeTrue();
            root.GetProperty("logsEnabled").GetBoolean().Should().BeTrue();
            root.GetProperty("otlpConfigured").GetBoolean().Should().BeFalse();
            root.GetProperty("otlpEndpointValid").GetBoolean().Should().BeFalse();
            root.GetProperty("otlpHeadersConfigured").GetBoolean().Should().BeFalse();
            root.GetProperty("otlpExporterState").GetString().Should().Be("notConfigured");
            root.GetProperty("traceExportState").GetString().Should().Be("notConfigured");
            root.GetProperty("metricsExportState").GetString().Should().Be("notConfigured");
            root.GetProperty("logExportState").GetString().Should().Be("notConfigured");
            root.GetProperty("lastExportError").ValueKind.Should().Be(JsonValueKind.Null);
            // #1168: probe state is exposed alongside exporter state so Console can distinguish
            // configuration-derived states from runtime reachability.
            root.GetProperty("otlpProbeState").GetString().Should().BeOneOf("unknown", "disabled");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task GetTelemetryStatus_WhenOtlpMisconfigured_ProbeStateReflectsExporterMisconfigured()
    {
        var fixture = CreateTelemetryFixture(new Dictionary<string, string?>
        {
            ["Tracing:Enabled"] = "true",
            ["Tracing:OtlpEndpoint"] = "not-a-uri",
            ["Tracing:OtlpHeaders"] = "",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = ""
        });

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var response = await client.GetAsync("/api/v1/admin/observability/telemetry");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("otlpExporterState").GetString().Should().Be("misconfigured");
            root.GetProperty("otlpProbeState").GetString().Should().Be("exporterMisconfigured");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task GetTelemetryStatus_WhenOtlpConfigured_ReturnsConfiguredExporterState()
    {
        var fixture = CreateTelemetryFixture(new Dictionary<string, string?>
        {
            ["Tracing:Enabled"] = "true",
            ["Tracing:OtlpEndpoint"] = "http://localhost:4317",
            ["Tracing:OtlpHeaders"] = "Authorization=Bearer test-token",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = ""
        });

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var response = await client.GetAsync("/api/v1/admin/observability/telemetry");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("tracingEnabled").GetBoolean().Should().BeTrue();
            root.GetProperty("otlpConfigured").GetBoolean().Should().BeTrue();
            root.GetProperty("otlpEndpointValid").GetBoolean().Should().BeTrue();
            root.GetProperty("otlpEndpoint").GetString().Should().Be("http://localhost:4317");
            root.GetProperty("otlpHeadersConfigured").GetBoolean().Should().BeTrue();
            root.GetProperty("otlpExporterState").GetString().Should().Be("configured");
            root.GetProperty("traceExportState").GetString().Should().Be("configured");
            root.GetProperty("metricsExportState").GetString().Should().Be("configured");
            root.GetProperty("logExportState").GetString().Should().Be("configured");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task GetTelemetryStatus_WhenOtlpEndpointInvalid_ReturnsMisconfiguredExporterState()
    {
        var fixture = CreateTelemetryFixture(new Dictionary<string, string?>
        {
            ["Tracing:Enabled"] = "true",
            ["Tracing:OtlpEndpoint"] = "not-a-uri",
            ["Tracing:OtlpHeaders"] = "",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = ""
        });

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var response = await client.GetAsync("/api/v1/admin/observability/telemetry");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("otlpConfigured").GetBoolean().Should().BeTrue();
            root.GetProperty("otlpEndpointValid").GetBoolean().Should().BeFalse();
            root.GetProperty("otlpExporterState").GetString().Should().Be("misconfigured");
            root.GetProperty("traceExportState").GetString().Should().Be("misconfigured");
            root.GetProperty("metricsExportState").GetString().Should().Be("misconfigured");
            root.GetProperty("logExportState").GetString().Should().Be("misconfigured");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task GetTelemetryStatus_WhenTracingDisabled_ReturnsDisabledStatus()
    {
        var disabledFixture = CreateTelemetryFixture(new Dictionary<string, string?>
        {
            ["Tracing:Enabled"] = "false",
            ["Tracing:OtlpEndpoint"] = "",
            ["Tracing:OtlpHeaders"] = "",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = ""
        });

        try
        {
            await disabledFixture.InitializeAsync();
            using var client = disabledFixture.CreateAdminClient();

            var response = await client.GetAsync("/api/v1/admin/observability/telemetry");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("tracingEnabled").GetBoolean().Should().BeFalse();
            root.GetProperty("metricsEnabled").GetBoolean().Should().BeTrue();
            root.GetProperty("logsEnabled").GetBoolean().Should().BeTrue();
            root.GetProperty("otlpConfigured").GetBoolean().Should().BeFalse();
            root.GetProperty("otlpExporterState").GetString().Should().Be("notConfigured");
            root.GetProperty("traceExportState").GetString().Should().Be("disabled");
            root.GetProperty("metricsExportState").GetString().Should().Be("notConfigured");
            root.GetProperty("logExportState").GetString().Should().Be("notConfigured");
        }
        finally
        {
            await disabledFixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateTelemetryFixture(IReadOnlyDictionary<string, string?> settings)
        => new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                foreach (var setting in settings)
                {
                    builder.UseSetting(setting.Key, setting.Value);
                }

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(settings);
                });
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(new StubDatabaseMigrationRunner());
            });

    private sealed class StubDatabaseMigrationRunner : IDatabaseMigrationRunner
    {
        public DatabaseMigrationPlan Plan { get; set; } = DatabaseMigrationPlan.Succeeded();
        public Exception? PlanException { get; set; }

        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
        {
            if (PlanException is not null)
            {
                throw PlanException;
            }

            return Task.FromResult(Plan);
        }

        public Task<DatabaseMigrationResult> RunMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }
}
