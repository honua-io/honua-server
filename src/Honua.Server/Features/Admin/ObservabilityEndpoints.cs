// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Monitoring;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for lightweight observability support.
/// </summary>
internal static class ObservabilityEndpoints
{
    private const string ExporterStateConfigured = "configured";
    private const string ExporterStateDisabled = "disabled";
    private const string ExporterStateMisconfigured = "misconfigured";
    private const string ExporterStateNotConfigured = "notConfigured";
    private const string ProbeStateUnknown = "unknown";
    private const string ProbeStateDisabled = "disabled";
    private const string ProbeStateHealthy = "healthy";
    private const string ProbeStateUnreachable = "unreachable";
    private const string ProbeStateAuthFailure = "authFailure";
    private const string ProbeStateExporterMisconfigured = "exporterMisconfigured";
    private const string ProbeStateTelemetryDisabled = "telemetryDisabled";
    private const string MigrationPlanUnavailableMessage = "Migration planning is temporarily unavailable.";

    public static void MapAdminObservabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/observability")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability")
            .RequireAdminAuthorization();

        group.MapGet("/errors", HandleGetRecentErrors)
            .WithDisplayName("Get Recent Errors")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<RecentErrorsResponse>();

        group.MapGet("/telemetry", HandleGetTelemetryStatus)
            .WithDisplayName("Get Telemetry Status")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ObservabilityStatusResponse>();

        group.MapGet("/migrations", HandleGetMigrationStatus)
            .WithDisplayName("Get Migration Status")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<MigrationObservabilityResponse>();
    }

    private static IResult HandleGetRecentErrors([FromServices] RecentErrorBuffer buffer)
    {
        var response = new RecentErrorsResponse
        {
            Capacity = buffer.Capacity,
            InstanceId = buffer.InstanceId,
            Errors = buffer.Snapshot()
        };

        return Results.Json(response, MetricsJsonContext.Default.RecentErrorsResponse);
    }

    private static IResult HandleGetTelemetryStatus(
        [FromServices] IOptions<TracingOptions> options,
        [FromServices] IConfiguration configuration,
        [FromServices] BackgroundOtlpProbe? probe = null)
    {
        var tracingOptions = options.Value;
        var otlpEndpoint = ResolveOtlpEndpoint(tracingOptions, configuration);
        var otlpHeaders = ResolveOtlpHeaders(tracingOptions, configuration);
        var otlpConfigured = !string.IsNullOrWhiteSpace(otlpEndpoint);
        var otlpEndpointValid = IsValidOtlpEndpoint(otlpEndpoint);
        var telemetryPipelineConfigured = IsTelemetryPipelineConfigured(configuration);
        var otlpExporterState = telemetryPipelineConfigured
            ? ResolveOtlpExporterState(otlpConfigured, otlpEndpointValid)
            : ExporterStateDisabled;
        var probeSnapshot = probe?.Snapshot;
        var probeState = ResolveProbeState(probeSnapshot, telemetryPipelineConfigured, otlpExporterState);

        var response = new ObservabilityStatusResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Realtime = new ObservabilityRealtimeStatus
            {
                Supported = true,
                HubPath = AdminRealtimeContract.HubPath,
                Protocol = AdminRealtimeContract.Protocol,
                Events = [AdminRealtimeContract.StatusChangedEventName]
            },
            TracingEnabled = tracingOptions.Enabled,
            MetricsEnabled = telemetryPipelineConfigured,
            LogsEnabled = telemetryPipelineConfigured,
            OtlpConfigured = otlpConfigured,
            OtlpEndpointValid = otlpEndpointValid,
            OtlpEndpoint = otlpConfigured ? otlpEndpoint : null,
            OtlpHeadersConfigured = !string.IsNullOrWhiteSpace(otlpHeaders),
            OtlpExporterState = otlpExporterState,
            TraceExportState = tracingOptions.Enabled ? otlpExporterState : ExporterStateDisabled,
            MetricsExportState = telemetryPipelineConfigured ? otlpExporterState : ExporterStateDisabled,
            LogExportState = telemetryPipelineConfigured ? otlpExporterState : ExporterStateDisabled,
            LastExportError = null,
            OtlpProbeState = probeState,
            OtlpLastProbeError = probeSnapshot?.LastError,
            OtlpLastProbedAt = probeSnapshot?.ProbedAt
        };

        return Results.Json(response, MetricsJsonContext.Default.ObservabilityStatusResponse);
    }

    private static string ResolveProbeState(
        OtlpProbeSnapshot? snapshot,
        bool telemetryPipelineConfigured,
        string otlpExporterState)
    {
        if (!telemetryPipelineConfigured)
        {
            return ProbeStateTelemetryDisabled;
        }

        if (otlpExporterState == ExporterStateMisconfigured)
        {
            return ProbeStateExporterMisconfigured;
        }

        if (snapshot is null)
        {
            return ProbeStateUnknown;
        }

        return snapshot.State switch
        {
            OtlpProbeState.Disabled => ProbeStateDisabled,
            OtlpProbeState.Healthy => ProbeStateHealthy,
            OtlpProbeState.Unreachable => ProbeStateUnreachable,
            OtlpProbeState.AuthFailure => ProbeStateAuthFailure,
            _ => ProbeStateUnknown
        };
    }

    private static async Task<IResult> HandleGetMigrationStatus(
        [FromServices] IConfiguration configuration,
        [FromServices] IDatabaseMigrationRunner migrationRunner,
        [FromServices] MigrationState migrationState,
        HttpContext context,
        [FromServices] IConnectionSecretResolver? secretResolver = null)
    {
        var response = new MigrationObservabilityResponse
        {
            Status = GetMigrationStatusLabel(migrationState.Status),
            IsReady = migrationState.IsReady,
            IsFailed = migrationState.IsFailed,
            Message = GetMigrationStatusMessage(migrationState),
            GeneratedAt = DateTimeOffset.UtcNow
        };

        var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
            configuration,
            secretResolver,
            context.RequestAborted).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Results.Json(
                response with
                {
                    PlanAvailable = false,
                    PlanError = "No database connection string configured."
                },
                MetricsJsonContext.Default.MigrationObservabilityResponse);
        }

        var plan = await migrationRunner.PlanMigrationsAsync(
            connectionString,
            typeof(Program).Assembly,
            context.RequestAborted).ConfigureAwait(false);

        return Results.Json(
            response with
            {
                PlanAvailable = plan.Successful,
                UpgradeRequired = plan.UpgradeRequired,
                PendingScripts = plan.PendingScripts,
                ExecutedButNotDiscoveredScripts = plan.ExecutedButNotDiscoveredScripts,
                PlanError = plan.Successful ? null : MigrationPlanUnavailableMessage
            },
            MetricsJsonContext.Default.MigrationObservabilityResponse);
    }

    private static string? ResolveOtlpEndpoint(TracingOptions tracingOptions, IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(tracingOptions.OtlpEndpoint))
        {
            return tracingOptions.OtlpEndpoint;
        }

        return configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    }

    private static string? ResolveOtlpHeaders(TracingOptions tracingOptions, IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(tracingOptions.OtlpHeaders))
        {
            return tracingOptions.OtlpHeaders;
        }

        return configuration["OTEL_EXPORTER_OTLP_HEADERS"];
    }

    private static bool IsTelemetryPipelineConfigured(IConfiguration configuration)
    {
        var tracingSection = configuration.GetSection(TracingOptions.SectionName);
        return tracingSection.Exists() || tracingSection.GetValue<bool>(nameof(TracingOptions.Enabled));
    }

    private static bool IsValidOtlpEndpoint(string? otlpEndpoint)
        => !string.IsNullOrWhiteSpace(otlpEndpoint) &&
           Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string ResolveOtlpExporterState(bool otlpConfigured, bool otlpEndpointValid)
    {
        if (!otlpConfigured)
        {
            return ExporterStateNotConfigured;
        }

        return otlpEndpointValid ? ExporterStateConfigured : ExporterStateMisconfigured;
    }

    private static string GetMigrationStatusLabel(MigrationLifecycleStatus status) =>
        status switch
        {
            MigrationLifecycleStatus.Running => "running",
            MigrationLifecycleStatus.Succeeded => "succeeded",
            MigrationLifecycleStatus.Skipped => "skipped",
            MigrationLifecycleStatus.Failed => "failed",
            _ => "unknown"
        };

    private static string? GetMigrationStatusMessage(MigrationState migrationState)
    {
        if (!string.IsNullOrWhiteSpace(migrationState.StatusMessage))
        {
            return migrationState.StatusMessage;
        }

        return migrationState.Status switch
        {
            MigrationLifecycleStatus.Running => "Database migrations in progress.",
            MigrationLifecycleStatus.Failed => "Database migrations failed.",
            MigrationLifecycleStatus.Unknown => "Database migrations not completed.",
            _ => null
        };
    }
}
