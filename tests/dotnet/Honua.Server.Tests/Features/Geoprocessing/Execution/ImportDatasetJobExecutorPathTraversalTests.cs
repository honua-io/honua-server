// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit-level regression for BH3-027: <c>ImportDatasetJobExecutor</c> must reject
/// any <c>sourcePath</c> job input that resolves to a path outside the configured
/// staging root directory.  These tests run without Docker / Testcontainers because
/// the path-validation logic is pure in-process and requires no external services.
/// </summary>
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class ImportDatasetJobExecutorPathTraversalTests
{
    // ─── BH3-027 regression ──────────────────────────────────────────────────────

    /// <summary>
    /// A <c>sourcePath</c> that resolves to a location outside the staging root must be
    /// rejected immediately — before any file I/O — with a <c>Failed</c> job status.
    /// Before the fix the executor passed the path verbatim to <see cref="File.Exists"/>
    /// and then to the import pipeline, allowing server-side path traversal by any
    /// authenticated caller with GP execution rights.
    /// </summary>
    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/processes/{processId}/execution")]
    public async Task ExecuteAsync_SourcePathOutsideStagingRoot_FailsWithPathTraversalError()
    {
        // Staging root = a unique subdirectory of the system temp folder.
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"honua-staging-unit-{Guid.NewGuid():N}");

        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions
        {
            ImportStagingDirectory = stagingRoot
        });

        var executor = new ImportDatasetJobExecutor(
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<ImportDatasetJobExecutor>.Instance,
            options);

        // Supply a traversal path that is a sibling of the staging root (not a descendant).
        var traversalPath = Path.Combine(Path.GetTempPath(), "honua-traversal-target-unit.txt");
        File.WriteAllText(traversalPath, "should not be read");
        try
        {
            var job = BuildJobRecord(traversalPath);
            var context = new NullJobExecutionContext("op-traversal-unit");

            var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

            result.Status.Should().Be(ExecutionJobStatus.Failed,
                "a source path outside the staging root must be rejected before any file access");
            result.ErrorMessage.Should().Contain("staging directory",
                "the rejection message must identify the staging-directory constraint (BH3-027)");
        }
        finally
        {
            if (File.Exists(traversalPath))
            {
                File.Delete(traversalPath);
            }

            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static ExecutionJobRecord BuildJobRecord(string sourcePath)
    {
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = ImportDatasetJobExecutor.HandledProcessId,
            ["protocolProcessId"] = ImportDatasetJobExecutor.HandledProcessId,
            [prefix + "connection"] = Guid.NewGuid().ToString(),
            [prefix + "sourcePath"] = sourcePath,
            [prefix + "fileName"] = "traversal.txt",
            [prefix + "tableName"] = "traversal_table",
            [prefix + "layerName"] = "Traversal Layer",
            [prefix + "serviceName"] = "traversal-service",
            [prefix + "targetSrid"] = "4326"
        };

        return new ExecutionJobRecord
        {
            OperationId = "op-traversal-unit",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:import.dataset",
                Parameters = parameters
            }
        };
    }

    /// <summary>No-op job execution context for path-traversal unit tests.</summary>
    private sealed class NullJobExecutionContext(string operationId) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;

        public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
