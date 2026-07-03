// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Geoprocessing.Testing.Tests;

/// <summary>
/// Unit tests for S1 security hardening: SSRF validation on WFS/OGC-features
/// remote sources (PA-194) and terminal-callback status correctness when the
/// result-package store write fails (PA-209).
/// </summary>
public sealed class GeoprocessingHardeningTests
{
    // Step-input parameter prefix for step ordinal 0 (matches ExecutionJobParameterKeys internals).
    private const string StepInputPrefix = "honua.geoprocessing.step.0.";

    // GP process-definitions parameter key (matches ExecutionJobParameterKeys internals).
    private const string ProcessDefinitionsKey = "honua.geoprocessing.process_definitions";

    // -----------------------------------------------------------------------
    // PA-194: SSRF guard on source.wfs and source.ogc-features
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("source.wfs", "https://192.168.1.1/wfs")]
    [InlineData("source.wfs", "https://10.0.0.1/wfs")]
    [InlineData("source.wfs", "https://169.254.169.254/latest/meta-data")]
    [InlineData("source.wfs", "https://172.16.0.1/wfs")]
    [InlineData("source.ogc-features", "https://192.168.100.1/ows")]
    [InlineData("source.ogc-features", "https://10.10.10.10/ogcapi")]
    public async Task RemoteSource_BlockedServiceUrl_ReturnsFailed(string processId, string serviceUrl)
    {
        var executor = BuildRemoteSourceExecutor(processId);
        var job = BuildJob(processId, serviceUrl);
        var context = BuildContext();

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed,
            "the SSRF guard must reject URLs that resolve to private/reserved addresses");
        result.ErrorMessage.Should().Contain("serviceUrl",
            "the error should name the offending input");
    }

    [Theory]
    [InlineData("source.wfs")]
    [InlineData("source.ogc-features")]
    public async Task RemoteSource_MissingServiceUrl_ReturnsFailed(string processId)
    {
        var executor = BuildRemoteSourceExecutor(processId);
        var job = BuildJob(processId, serviceUrl: null);
        var context = BuildContext();

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed,
            "serviceUrl is required for remote HTTP sources");
        result.ErrorMessage.Should().Contain("serviceUrl");
    }

    // -----------------------------------------------------------------------
    // PA-209: terminal callback must set Failed when result-package SetAsync throws
    // -----------------------------------------------------------------------

    [UnitTest]
    public async Task TerminalCallback_ResultPackageSetAsyncThrows_ProgressMarkedFailed()
    {
        const string operationId = "test-pa209-op";

        // --- mocks ---
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var processCatalog = Substitute.For<IProcessCatalog>();
        var resultPackageStore = Substitute.For<IGeoprocessingResultPackageStore>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions
        {
            ResultRetention = TimeSpan.FromDays(1)
        });

        // Simulate a transient storage failure on the result-package write.
        resultPackageStore
            .SetAsync(
                Arg.Any<string>(),
                Arg.Any<AnalysisResultPackage>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Storage backend unavailable")));

        // Return a non-terminal (Running) progress so the callback does not bail out early.
        var runningProgress = new GeoprocessingProgress
        {
            OperationId = operationId,
            WorkflowStatus = GeoprocessingWorkflowStatus.Running,
            CurrentStage = GeoprocessingStageKind.Execute,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        progressStore
            .GetProgressAsync<GeoprocessingProgress>(operationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GeoprocessingProgress?>(runningProgress));

        var callback = new GeoprocessingJobTerminalCallback(
            progressStore,
            processCatalog,
            options,
            resultPackageStore,
            scopeFactory,
            NullLogger<GeoprocessingJobTerminalCallback>.Instance);

        // Succeeded job — without the fix the outer catch would leave
        // artifactPersistenceError null, so effectiveStatus stays Succeeded
        // and the progress would be written as Completed (wrong).
        var job = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "managed",
                WorkloadName = "test-workload",
                Parameters = new Dictionary<string, string>
                {
                    // Include process definitions so the factory does not need catalog lookups.
                    [ProcessDefinitionsKey] = "geometry.buffer"
                }
            }
        };

        // --- act ---
        await callback.OnTerminalAsync(job, CancellationToken.None);

        // --- assert ---
        // After the fix the outer catch sets artifactPersistenceError, so
        // effectiveStatus flips to Failed and progress must be written as Failed.
        await progressStore.Received(1).SetProgressAsync(
            operationId,
            Arg.Is<IOperationProgress>(p =>
                ((GeoprocessingProgress)p).WorkflowStatus == GeoprocessingWorkflowStatus.Failed),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static RemoteSourceExecutor BuildRemoteSourceExecutor(string processId)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7),
        });
        return RemoteSourceExecutor.ForProcess(
            processId,
            scopeFactory,
            options,
            NullLogger<RemoteSourceExecutor>.Instance);
    }

    private static ExecutionJobRecord BuildJob(string processId, string? serviceUrl)
    {
        var parameters = new Dictionary<string, string>
        {
            [ProcessDefinitionsKey] = processId
        };
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            parameters[StepInputPrefix + "serviceUrl"] = serviceUrl;
        }

        return new ExecutionJobRecord
        {
            OperationId = "test-job-" + Guid.NewGuid().ToString("N"),
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "managed",
                WorkloadName = "test-workload",
                Parameters = parameters
            }
        };
    }

    private static IJobExecutionContext BuildContext()
    {
        var context = Substitute.For<IJobExecutionContext>();
        context
            .ReportProgressAsync(Arg.Any<double?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return context;
    }
}
