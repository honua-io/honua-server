// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Test helpers for building execution-job records and options for the GDAL
/// worker executors.
/// </summary>
internal static class GdalJobFactory
{
    public static ExecutionJobRecord Job(string processId, params (string Name, string Value)[] inputs)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.ProcessDefinitions] = processId,
        };

        foreach (var (name, value) in inputs)
        {
            parameters[GdalWorkerParameterKeys.StepInputPrefix + name] = value;
        }

        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "test-" + Guid.NewGuid().ToString("N"),
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "local",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = processId,
                RuntimeProfile = GdalWorkerParameterKeys.NativeRuntimeProfile,
                Parameters = parameters,
            },
        };
    }

    public static IOptionsMonitor<GdalWorkerOptions> Options(string scratchRoot)
        => new StaticOptionsMonitor<GdalWorkerOptions>(new GdalWorkerOptions
        {
            ScratchRoot = scratchRoot,
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ToolTimeout = TimeSpan.FromMinutes(1),
        });

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
