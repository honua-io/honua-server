// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Unit coverage for the server-side MCP operational-observability adapter (#2555).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class McpOpsObservabilityReaderTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetOpsHealth_Authorized_UsesOpsReadPolicyAndReturnsSnapshot()
    {
        var principal = CreatePrincipal();
        var health = Substitute.For<IOpsHealthSnapshotService>();
        var authorization = CreateAuthorization(AuthorizationResult.Success());
        health.GetAsync(Arg.Any<CancellationToken>()).Returns(CreateHealthSnapshot());

        using var services = CreateServices();
        var reader = CreateReader(health, authorization, services);

        var result = await reader.GetOpsHealthAsync(principal, CancellationToken.None);

        result.GetProperty("overallStatus").GetString().Should().Be("Healthy");
        await authorization.Received(1).AuthorizeAsync(
            principal,
            Arg.Is<object>(resource => IsOpsReadResource(resource, principal)),
            AuthenticationExtensions.OpsReadPolicy);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetOpsHealth_Unauthorized_ThrowsBeforeReadingSnapshot()
    {
        var principal = CreatePrincipal();
        var health = Substitute.For<IOpsHealthSnapshotService>();
        var authorization = CreateAuthorization(AuthorizationResult.Failed());

        using var services = CreateServices();
        var reader = CreateReader(health, authorization, services);

        var act = () => reader.GetOpsHealthAsync(principal, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await health.DidNotReceive().GetAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ListAlertEvents_TranslatesFiltersAndReturnsMappedPage()
    {
        var principal = CreatePrincipal();
        var authorization = CreateAuthorization(AuthorizationResult.Success());
        var alertQuery = Substitute.For<IAlertEventQuery>();
        AlertEventFilter? capturedFilter = null;
        alertQuery.ListAsync(Arg.Any<AlertEventFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedFilter = call.Arg<AlertEventFilter>();
                return new AlertEventPage
                {
                    Items =
                    [
                        new AlertEventSummary
                        {
                            EventId = 42,
                            RuleId = 17,
                            RuleName = "geofence-breach",
                            ServiceId = "vessels",
                            LayerId = 0,
                            ObjectId = 100821,
                            TriggerType = AlertTriggerType.Enter,
                            Severity = AlertSeverity.Critical,
                            OccurredAt = DateTimeOffset.Parse(
                                "2026-07-06T14:22:10Z",
                                CultureInfo.InvariantCulture),
                            IncidentStatus = AlertIncidentStatus.Ongoing,
                            IncidentDurationMs = 45_000,
                            LifecycleStatus = AlertLifecycleStatus.Open,
                        },
                    ],
                    NextCursor = "cursor-2",
                };
            });

        using var services = CreateServices(alertQuery);
        var reader = CreateReader(authorization: authorization, services: services);
        var argument = new McpAlertEventsArgument
        {
            Source = "gis",
            Severity = "critical",
            Rule = "17",
            LifecycleState = "open",
            From = DateTimeOffset.Parse("2026-07-01T00:00:00Z", CultureInfo.InvariantCulture),
            To = DateTimeOffset.Parse("2026-07-02T00:00:00Z", CultureInfo.InvariantCulture),
            PageSize = 999,
            Cursor = "cursor-1",
        };

        var result = await reader.ListAlertEventsAsync(principal, argument, CancellationToken.None);

        capturedFilter.Should().NotBeNull();
        capturedFilter!.RuleId.Should().Be(17);
        capturedFilter.Severities.Should().BeEquivalentTo([AlertSeverity.Critical]);
        capturedFilter.LifecycleStatuses.Should().BeEquivalentTo([AlertLifecycleStatus.Open]);
        capturedFilter.PageSize.Should().Be(200);
        capturedFilter.Cursor.Should().Be("cursor-1");
        result.GetProperty("nextCursor").GetString().Should().Be("cursor-2");
        var item = result.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("eventId").GetInt64().Should().Be(42);
        item.GetProperty("resourceRef").GetString().Should().Be("alert/42");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ListOperateEvents_TranslatesFiltersAndReturnsMappedPage()
    {
        var principal = CreatePrincipal();
        var authorization = CreateAuthorization(AuthorizationResult.Success());
        var operateEvents = Substitute.For<IOperateEventFeed>();
        OperateEventFilter? capturedFilter = null;
        operateEvents.ListAsync(Arg.Any<OperateEventFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedFilter = call.Arg<OperateEventFilter>();
                return new OperateEventPage
                {
                    Items =
                    [
                        new OperateEvent
                        {
                            EventId = "release:2026.07.01",
                            Kind = OperateEventKind.Release,
                            Severity = OperateEventSeverity.Notice,
                            OccurredAt = DateTimeOffset.Parse(
                                "2026-07-06T14:20:00Z",
                                CultureInfo.InvariantCulture),
                            Title = "Release promoted",
                            CorrelationId = "corr-1",
                            OperationId = "op-1",
                            ReleaseId = "2026.07.01",
                            ResourceRef = "release/2026.07.01",
                        },
                    ],
                    PartialResult = true,
                    SourceErrors = new Dictionary<OperateEventKind, string>
                    {
                        [OperateEventKind.Log] = "log source unavailable",
                    },
                };
            });

        using var services = CreateServices();
        var reader = CreateReader(authorization: authorization, operateEvents: operateEvents, services: services);
        var argument = new McpOperateEventsArgument
        {
            Kind = ["release", "job"],
            CorrelationId = " corr-1 ",
            OperationId = "op-1",
            ReleaseId = "2026.07.01",
            PageSize = 0,
        };

        var result = await reader.ListOperateEventsAsync(principal, argument, CancellationToken.None);

        capturedFilter.Should().NotBeNull();
        capturedFilter!.Kinds.Should().BeEquivalentTo([OperateEventKind.Release, OperateEventKind.Job]);
        capturedFilter.CorrelationId.Should().Be("corr-1");
        capturedFilter.OperationId.Should().Be("op-1");
        capturedFilter.ReleaseId.Should().Be("2026.07.01");
        capturedFilter.PageSize.Should().Be(1);
        result.GetProperty("partialResult").GetBoolean().Should().BeTrue();
        result.GetProperty("sourceErrors").GetProperty("log").GetString().Should().Be("log source unavailable");
        var item = result.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("kind").GetString().Should().Be("release");
    }

    private static McpOpsObservabilityReader CreateReader(
        IOpsHealthSnapshotService? health = null,
        IAuthorizationService? authorization = null,
        IServiceProvider? services = null,
        IOpsFindingsService? findings = null,
        IOperateEventFeed? operateEvents = null) =>
        new(
            health ?? Substitute.For<IOpsHealthSnapshotService>(),
            findings ?? Substitute.For<IOpsFindingsService>(),
            operateEvents ?? Substitute.For<IOperateEventFeed>(),
            authorization ?? CreateAuthorization(AuthorizationResult.Success()),
            services ?? CreateServices());

    private static IAuthorizationService CreateAuthorization(AuthorizationResult result)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object>(),
                AuthenticationExtensions.OpsReadPolicy)
            .Returns(result);
        return authorization;
    }

    private static ServiceProvider CreateServices(IAlertEventQuery? alertQuery = null)
    {
        var services = new ServiceCollection();
        if (alertQuery is not null)
        {
            services.AddSingleton(alertQuery);
        }

        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal CreatePrincipal() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ops-reader")], "test"));

    private static bool IsOpsReadResource(object resource, ClaimsPrincipal principal) =>
        resource is DefaultHttpContext context &&
        ReferenceEquals(context.User, principal) &&
        string.Equals(context.Request.Method, HttpMethods.Get, StringComparison.Ordinal);

    private static OpsHealthSnapshotResponse CreateHealthSnapshot() =>
        new()
        {
            GeneratedAt = DateTimeOffset.Parse("2026-07-06T14:00:00Z", CultureInfo.InvariantCulture),
            OverallStatus = "Healthy",
            Health = new OpsHealthChecksView
            {
                Status = "Healthy",
                TotalDurationMs = 1,
                Entries = [],
            },
            ServingLatency = new OpsServingLatencyView
            {
                WindowSeconds = 60,
                Protocols = [],
            },
            Geoprocessing = new OpsGpQueueView
            {
                TotalActive = 0,
                Available = true,
                Buckets = [],
            },
            AlertDispatch = new OpsAlertDispatchView
            {
                DispatcherRunning = true,
                DispatcherEnabled = true,
                StoragePollFailing = false,
            },
            Deploy = new OpsDeployReadinessView
            {
                Status = "ready",
                ReadyForCoordinatedDeploy = true,
                PendingMigrationsCount = 0,
                PendingContractScriptsCount = 0,
                PlatformRelease = new OpsPlatformReleaseView
                {
                    ReleaseDeclared = false,
                    IsCoVersioned = true,
                    SkewedIds = [],
                },
            },
            Database = new OpsDatabaseView
            {
                HasConnectionPoolData = false,
                ActiveConnections = 0,
                ConnectionAcquisitionTimeouts = 0,
                ConnectionAcquisitionFailures = 0,
                CacheHitRatio = 1,
                ErrorRate = 0,
            },
        };
}
