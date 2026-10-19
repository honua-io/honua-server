// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Geoprocessing;
using Honua.Server.Features.Admin.Jobs;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Admin.Jobs;

/// <summary>
/// Unit coverage for the server-side MCP job diagnostics adapter (honua-server#6219): it
/// reads the admin diagnostics projection under the caller's principal, separates observed
/// facts from inferred explanations, and keeps unknown and unreadable jobs indistinguishable.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpJobDiagnosticsReaderTests
{
    [UnitTest]
    [Operation(Operations.OperationsProgress)]
    public async Task DiagnoseJob_SplitsObservedFactsFromInferredExplanations()
    {
        var jobs = new StubConsoleJobService(Projection("gp-42"));
        var reader = new McpJobDiagnosticsReader(jobs);
        var principal = Principal();

        var result = await reader.DiagnoseJobAsync(principal, new McpDiagnoseJobArgument { JobId = " gp-42 ", Events = 9 }, CancellationToken.None);

        jobs.LastPrincipal.Should().BeSameAs(principal);
        jobs.LastJobId.Should().Be("gp-42");
        jobs.LastEventLimit.Should().Be(9);
        result.GetProperty("schema").GetString().Should().Be(McpJobDiagnosticsReader.DiagnosisSchemaId);
        result.GetProperty("jobId").GetString().Should().Be("gp-42");
        result.GetProperty("caveat").GetString().Should().Contain("not proof");

        var observed = result.GetProperty("observed");
        observed.GetProperty("status").GetString().Should().Be("Failed");
        observed.GetProperty("attemptNumber").GetInt32().Should().Be(2);
        observed.GetProperty("failure").GetProperty("message").GetString().Should().Be("gdalwarp failed");
        observed.GetProperty("failure").GetProperty("nativeExitCode").GetInt32().Should().Be(2);
        observed.GetProperty("failure").TryGetProperty("classification", out _).Should().BeFalse("classification is inferred, not observed");
        observed.GetProperty("worker").GetProperty("lastHeartbeatAt").GetString().Should().NotBeNullOrEmpty();
        observed.GetProperty("worker").TryGetProperty("liveness", out _).Should().BeFalse("liveness is a verdict, not a recorded fact");
        observed.GetProperty("release").GetProperty("state").GetString().Should().Be("partial");

        var inferred = result.GetProperty("inferred");
        inferred.GetProperty("phase").GetString().Should().Be("failed");
        inferred.GetProperty("liveness").GetString().Should().Be("finished");
        inferred.GetProperty("failureClassification").GetString().Should().Be("execution_failed");
        inferred.GetProperty("recovery").GetString().Should().Contain("retry");
        inferred.GetProperty("retry").GetProperty("allowed").GetBoolean().Should().BeTrue();

        result.GetProperty("evidence").GetProperty("missing")[0].GetString().Should().StartWith("release.sourceRef");
    }

    [UnitTest]
    [Operation(Operations.OperationsProgress)]
    public async Task GetJobEvidence_ReturnsStepsEventsAndArtifactsWithoutTheInferredLayer()
    {
        var jobs = new StubConsoleJobService(Projection("gp-42"));
        var reader = new McpJobDiagnosticsReader(jobs);

        var result = await reader.GetJobEvidenceAsync(Principal(), new McpJobEvidenceArgument { JobId = "gp-42", Events = 500, EventCursor = "start" }, CancellationToken.None);

        jobs.LastEventLimit.Should().Be(500);
        jobs.LastEventCursor.Should().Be("start");
        result.GetProperty("schema").GetString().Should().Be(McpJobDiagnosticsReader.EvidenceSchemaId);
        result.GetProperty("steps").GetArrayLength().Should().Be(1);
        result.GetProperty("events").GetProperty("totalCount").GetInt32().Should().Be(3);
        result.GetProperty("events").GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("artifacts").GetProperty("state").GetString().Should().Be("none");
        result.TryGetProperty("inferred", out _).Should().BeFalse();
        result.TryGetProperty("observed", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.OperationsProgress)]
    public async Task DiagnoseJob_UnknownOrUnreadableJob_IsNotFoundAndMissingJobIdIsRejected()
    {
        var jobs = new StubConsoleJobService(projection: null);
        var reader = new McpJobDiagnosticsReader(jobs);

        var notFound = () => reader.DiagnoseJobAsync(Principal(), new McpDiagnoseJobArgument { JobId = "gp-missing" }, CancellationToken.None);
        await notFound.Should().ThrowAsync<GeoprocessingNotFoundException>();

        var invalid = () => reader.GetJobEvidenceAsync(Principal(), new McpJobEvidenceArgument { JobId = "" }, CancellationToken.None);
        await invalid.Should().ThrowAsync<GeoprocessingValidationException>();
        jobs.Calls.Should().Be(1, "an empty jobId is rejected before the projection is read");
    }

    private static ClaimsPrincipal Principal()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "mcp-reader-test")], "Test"));

    private static ConsoleJobDiagnostics Projection(string jobId)
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var links = new ConsoleJobLinks
        {
            Self = $"/api/v1/admin/jobs/{jobId}",
            Logs = $"/api/v1/admin/jobs/{jobId}/logs",
            Artifacts = $"/api/v1/admin/jobs/{jobId}/artifacts",
            Actions = $"/api/v1/admin/jobs/{jobId}/actions",
            Steps = $"/api/v1/admin/jobs/{jobId}/steps",
            Diagnostics = $"/api/v1/admin/jobs/{jobId}/diagnostics"
        };

        return new ConsoleJobDiagnostics
        {
            JobId = jobId,
            CorrelationId = "corr-42",
            GeneratedAt = now,
            View = "operator",
            Identity = new ConsoleJobDiagnosticIdentity
            {
                Kind = "Geoprocessing",
                Backend = "local",
                TargetKind = "KubernetesJob",
                WorkloadName = "buffer",
                Priority = "Normal",
                ContractVersion = 1,
                ResourceRefs = []
            },
            Release = new ConsoleJobDiagnosticRelease
            {
                State = "partial",
                ServerVersion = "2026.1.0-test",
                Runtime = "python",
                ProcessDefinitions = [],
                Missing = ["release.sourceRef"]
            },
            Lifecycle = new ConsoleJobDiagnosticLifecycle
            {
                Status = "Failed",
                Phase = "failed",
                CurrentPhase = "Failed",
                CreatedAt = now.AddMinutes(-10),
                ClaimedAt = now.AddMinutes(-9),
                UpdatedAt = now.AddMinutes(-5),
                CompletedAt = now.AddMinutes(-5),
                QueueDelayMs = 60_000,
                ExecutionMs = 240_000,
                DurationMs = 300_000,
                TimingSource = "measured"
            },
            Attempt = new ConsoleJobDiagnosticAttempt
            {
                Number = 2,
                MaxAttempts = 3,
                Retry = new ConsoleJobDiagnosticRetry
                {
                    Allowed = true,
                    WouldRepeat = "re-dispatches the whole job as attempt 3 of 3 on backend 'local' with the same spec; no committed effects are recorded from earlier attempts"
                }
            },
            Worker = new ConsoleJobDiagnosticWorker
            {
                ClaimedBy = "worker-1",
                LastHeartbeatAt = now.AddMinutes(-5),
                HeartbeatIntervalSeconds = 30,
                HeartbeatTimeoutSeconds = 90,
                Liveness = "finished",
                LivenessReason = "job is Failed; no worker is expected to be alive"
            },
            Failure = new ConsoleJobDiagnosticFailure
            {
                Message = "gdalwarp failed",
                Classification = "execution_failed",
                Phase = "Reproject",
                StepOrdinal = 0,
                OccurredAt = now.AddMinutes(-5),
                ErrorCode = "GDAL_WARP_FAILED",
                NativeExitCode = 2,
                StderrExcerpt = "ERROR 1: could not open <path>",
                Recovery = "a step failed with native output; fix the input or tool before retrying, since a retry repeats the same spec"
            },
            Steps =
            [
                new ConsoleJobStep
                {
                    Ordinal = 0,
                    Phase = "Reproject",
                    Status = "Failed",
                    StartedAt = now.AddMinutes(-6),
                    CompletedAt = now.AddMinutes(-5),
                    DurationMs = 60_000,
                    Message = "gdalwarp failed for <path>"
                }
            ],
            Events = new ConsoleJobDiagnosticEvents
            {
                State = "available",
                Items = [],
                TotalCount = 3,
                Limit = 1,
                Truncated = true
            },
            Artifacts = new ConsoleJobDiagnosticArtifacts
            {
                State = "none",
                Items = [],
                AvailableCount = 0,
                ExpiredCount = 0,
                UnavailableCount = 0
            },
            Evidence = new ConsoleJobDiagnosticEvidence
            {
                Missing = ["release.sourceRef: not recorded on the job"],
                Truncated = ["events: 1 most recent of 3 returned; page the logs link for the rest"],
                Notes = []
            },
            Links = links
        };
    }

    private sealed class StubConsoleJobService(ConsoleJobDiagnostics? projection) : IConsoleJobService
    {
        public int Calls { get; private set; }

        public ClaimsPrincipal? LastPrincipal { get; private set; }

        public string? LastJobId { get; private set; }

        public int? LastEventLimit { get; private set; }

        public string? LastEventCursor { get; private set; }

        public Task<ConsoleJobDiagnostics?> GetDiagnosticsAsync(
            ClaimsPrincipal user,
            string pathBase,
            string jobId,
            int? eventLimit,
            string? eventCursor,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastPrincipal = user;
            LastJobId = jobId;
            LastEventLimit = eventLimit;
            LastEventCursor = eventCursor;
            return Task.FromResult(projection);
        }

        public Task<ConsoleJobDiagnostics?> GetDiagnosticsAsync(HttpContext context, string jobId, int? eventLimit, string? eventCursor, CancellationToken cancellationToken)
            => GetDiagnosticsAsync(context.User, string.Empty, jobId, eventLimit, eventCursor, cancellationToken);

        public Task<ConsoleJobListResponse> ListAsync(HttpContext context, ConsoleJobQueryRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConsoleJobDetail?> GetDetailAsync(HttpContext context, string jobId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConsoleJobLogPageResponse?> GetLogsAsync(HttpContext context, string jobId, string? cursor, int limit, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConsoleJobArtifactPageResponse?> GetArtifactsAsync(HttpContext context, string jobId, string? cursor, int limit, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConsoleJobActionsResponse?> GetActionsAsync(HttpContext context, string jobId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConsoleJobStepsResponse?> GetStepsAsync(HttpContext context, string jobId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConsoleJobControlResponse?> CancelAsync(HttpContext context, string jobId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConsoleJobControlResponse?> RetryAsync(HttpContext context, string jobId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
