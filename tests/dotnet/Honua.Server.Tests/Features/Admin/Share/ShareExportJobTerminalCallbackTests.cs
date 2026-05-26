// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Share.Domain;
using Honua.Server.Features.Admin.Share;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Admin.Share;

/// <summary>
/// Unit tests for <see cref="ShareExportJobTerminalCallback"/>, which reconciles a Share export run
/// with the terminal state of its backing execution job (#1216).
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Export)]
public sealed class ShareExportJobTerminalCallbackTests
{
    private const string ExportId = "terminal-export";
    private const string RunId = "terminal-run";
    private const string JobRunId = "share-export-terminal";

    private static readonly DateTimeOffset CompletedAt =
        DateTimeOffset.Parse("2026-05-25T06:30:00Z", CultureInfo.InvariantCulture);

    private static readonly string[] SucceededArtifacts =
    {
        "https://example.test/share/exports/parcels-2026-05-25.geojson",
        "s3://share-exports/parcels/2026-05-25.geojson"
    };

    private static readonly string[] FailedDiagnosticsArtifacts =
    {
        "https://example.test/share/exports/failed-diagnostics.json"
    };

    private static readonly string[] PreexistingArtifacts =
    {
        "https://example.test/share/exports/preexisting.geojson"
    };

    [UnitTest]
    public async Task OnTerminal_SucceededJob_MarksRunSucceeded()
    {
        var run = await ReconcileAsync(ExecutionJobStatus.Succeeded);

        run.Should().NotBeNull();
        run!.Status.Should().Be(ShareExportRunStatus.Succeeded);
        run.CompletedAt.Should().Be(CompletedAt);
        run.LastError.Should().BeNull();
        // The job published no artifacts and the run had none, so none are fabricated.
        run.ResultArtifacts.Should().BeEmpty();
    }

    [UnitTest]
    public async Task OnTerminal_SucceededJobWithArtifacts_CopiesArtifactsToRun()
    {
        // A successful worker publishes its output references on the backing job; the run history
        // must surface them so Console run detail matches the Operate job (the cited finding).
        var run = await ReconcileAsync(
            ExecutionJobStatus.Succeeded,
            jobArtifacts: SucceededArtifacts);

        run.Should().NotBeNull();
        run!.Status.Should().Be(ShareExportRunStatus.Succeeded);
        run.ResultArtifacts.Should().Equal(SucceededArtifacts);
    }

    [UnitTest]
    public async Task OnTerminal_FailedJobWithArtifacts_CopiesDiagnosticsArtifacts()
    {
        // Failed jobs can still publish diagnostics artifacts; reconciliation copies them rather
        // than gating artifact propagation on success.
        var run = await ReconcileAsync(
            ExecutionJobStatus.Failed,
            jobErrorMessage: "destination rejected delivery",
            jobArtifacts: FailedDiagnosticsArtifacts);

        run.Should().NotBeNull();
        run!.Status.Should().Be(ShareExportRunStatus.Failed);
        run.LastError.Should().Be("destination rejected delivery");
        run.ResultArtifacts.Should().Equal(FailedDiagnosticsArtifacts);
    }

    [UnitTest]
    public async Task OnTerminal_JobWithoutArtifacts_PreservesExistingRunArtifacts()
    {
        // When the backing job published no artifacts, any already recorded on the run are kept
        // rather than being cleared by the reconcile.
        var run = await ReconcileAsync(
            ExecutionJobStatus.Succeeded,
            seededArtifacts: PreexistingArtifacts);

        run.Should().NotBeNull();
        run!.Status.Should().Be(ShareExportRunStatus.Succeeded);
        run.ResultArtifacts.Should().Equal(PreexistingArtifacts);
    }

    [UnitTest]
    public async Task OnTerminal_FailedJob_MarksRunFailedWithJobError()
    {
        var run = await ReconcileAsync(ExecutionJobStatus.Failed, jobErrorMessage: "destination rejected delivery");

        run.Should().NotBeNull();
        run!.Status.Should().Be(ShareExportRunStatus.Failed);
        run.CompletedAt.Should().Be(CompletedAt);
        run.LastError.Should().Be("destination rejected delivery");
    }

    [UnitTest]
    public async Task OnTerminal_CancelledJob_MarksRunCancelled()
    {
        var run = await ReconcileAsync(ExecutionJobStatus.Cancelled);

        run.Should().NotBeNull();
        run!.Status.Should().Be(ShareExportRunStatus.Cancelled);
        run.CompletedAt.Should().Be(CompletedAt);
    }

    [UnitTest]
    public async Task OnTerminal_NonShareExportKind_LeavesRunUnchanged()
    {
        var run = await ReconcileAsync(ExecutionJobStatus.Succeeded, jobKind: ExecutionJobKind.Geoprocessing);

        run.Should().NotBeNull();
        run!.Status.Should().Be(ShareExportRunStatus.Queued);
    }

    [UnitTest]
    public async Task OnTerminal_AlreadyTerminalRun_IsNotOverwritten()
    {
        // A run that is already terminal (e.g. dispatch-failed) must not be clobbered by a later
        // terminal notification for the same backing job.
        var run = await ReconcileAsync(
            ExecutionJobStatus.Succeeded,
            seededStatus: ShareExportRunStatus.Failed,
            seededError: "share-export-dispatch-failed");

        run.Should().NotBeNull();
        run!.Status.Should().Be(ShareExportRunStatus.Failed);
        run.LastError.Should().Be("share-export-dispatch-failed");
    }

    private static async Task<ShareExportRun?> ReconcileAsync(
        ExecutionJobStatus jobStatus,
        string? jobErrorMessage = null,
        ShareExportRunStatus seededStatus = ShareExportRunStatus.Queued,
        string? seededError = null,
        ExecutionJobKind jobKind = ExecutionJobKind.ShareExport,
        IReadOnlyList<string>? jobArtifacts = null,
        IReadOnlyList<string>? seededArtifacts = null)
    {
        var store = new InMemoryShareExportStore();
        await store.CreateDefinitionAsync(BuildDefinition());
        await store.AppendRunAsync(BuildRun(seededStatus, seededError, seededArtifacts));

        var services = new ServiceCollection();
        services.AddSingleton<IShareExportStore>(store);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILogger<ShareExportJobTerminalCallback>>(
            NullLogger<ShareExportJobTerminalCallback>.Instance);
        services.AddSingleton<IJobTerminalCallback, ShareExportJobTerminalCallback>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var callback = provider.GetRequiredService<IJobTerminalCallback>();
        await callback.OnTerminalAsync(
            BuildJob(jobKind, jobStatus, jobErrorMessage, jobArtifacts),
            CancellationToken.None);

        return await store.GetRunAsync(ExportId, RunId);
    }

    private static ShareExportDefinition BuildDefinition()
        => new()
        {
            ExportId = ExportId,
            ResourceId = "content-terminal",
            ServiceName = "terminal-layer",
            LayerId = 7,
            DisplayName = "Terminal reconcile export",
            DestinationType = ShareExportDestinationType.Webhook,
            DestinationStatus = ShareExportDestinationStatus.Supported,
            DestinationConfig = new Dictionary<string, string> { ["url"] = "https://example.invalid/webhook" },
            Format = "GeoJSON",
            Schedule = "0 * * * *",
            ScheduleState = ShareExportScheduleState.Active,
            CreatedAt = DateTimeOffset.Parse("2026-05-25T06:00:00Z", CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse("2026-05-25T06:00:00Z", CultureInfo.InvariantCulture)
        };

    private static ShareExportRun BuildRun(
        ShareExportRunStatus status,
        string? lastError,
        IReadOnlyList<string>? resultArtifacts = null)
        => new()
        {
            RunId = RunId,
            ExportId = ExportId,
            TriggerKind = ShareExportTriggerKind.Manual,
            Status = status,
            JobRunId = JobRunId,
            TriggeredAt = DateTimeOffset.Parse("2026-05-25T06:15:00Z", CultureInfo.InvariantCulture),
            StartedAt = null,
            CompletedAt = status is ShareExportRunStatus.Succeeded or ShareExportRunStatus.Failed or ShareExportRunStatus.Cancelled
                ? DateTimeOffset.Parse("2026-05-25T06:15:00Z", CultureInfo.InvariantCulture)
                : null,
            TargetSummary = "Webhook https://example.invalid/webhook",
            ResultArtifacts = resultArtifacts ?? Array.Empty<string>(),
            LastError = lastError
        };

    private static ExecutionJobRecord BuildJob(
        ExecutionJobKind kind,
        ExecutionJobStatus status,
        string? errorMessage,
        IReadOnlyList<string>? artifactReferences = null)
        => new()
        {
            OperationId = JobRunId,
            Status = status,
            CreatedAt = DateTimeOffset.Parse("2026-05-25T06:15:00Z", CultureInfo.InvariantCulture),
            UpdatedAt = CompletedAt,
            CompletedAt = CompletedAt,
            ErrorMessage = errorMessage,
            ArtifactReferences = artifactReferences ?? Array.Empty<string>(),
            Spec = new ExecutionJobSpec
            {
                Kind = kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = $"share-export:{ExportId}",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ExecutionJobParameterKeys.ShareExportId] = ExportId,
                    [ExecutionJobParameterKeys.ShareRunId] = RunId
                }
            }
        };
}
