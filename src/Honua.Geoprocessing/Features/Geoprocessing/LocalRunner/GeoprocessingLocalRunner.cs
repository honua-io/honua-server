// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;

namespace Honua.Geoprocessing.LocalRunner;

public sealed class GeoprocessingLocalRunner
{
    private readonly IReadOnlyDictionary<string, IProcessExecutor> _executorsById;

    public GeoprocessingLocalRunner(IEnumerable<IProcessExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _executorsById = ProcessExecutorRouteTable.Build(executors);
    }

    public IReadOnlyList<string> AvailableProcessIds =>
        _executorsById.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();

    public Task<LocalRunResult> RunAsync(
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken = default)
        => RunAsync(processId, inputs, glassBox: null, cancellationToken);

    public async Task<LocalRunResult> RunAsync(
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        GlassBoxCapture? glassBox,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!_executorsById.TryGetValue(processId, out var executor))
        {
            var supported = string.Join(", ", AvailableProcessIds);
            return LocalRunResult.UnknownProcess(processId, supported);
        }

        var record = BuildJobRecord(processId, inputs, executor);
        var context = new LocalJobExecutionContext(record.OperationId);

        var stopwatch = Stopwatch.StartNew();
        JobExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(record, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Fatal CLR exceptions must keep unwinding rather than being converted into a
        // "failed" run result — the process is not in a state where continuing to serve
        // requests (or reporting a normal job failure) is meaningful.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            stopwatch.Stop();
            return new LocalRunResult
            {
                ProcessId = processId,
                Status = ExecutionJobStatus.Failed,
                ErrorMessage = $"Executor threw {ex.GetType().Name}: {ex.Message}",
                Artifacts = context.Artifacts,
                Logs = context.Logs,
                Warnings = [],
                Phases = context.Phases,
                Elapsed = stopwatch.Elapsed,
                GlassBox = BuildGlassBox(glassBox, context),
            };
        }

        stopwatch.Stop();

        return new LocalRunResult
        {
            ProcessId = processId,
            Status = result.Status,
            ErrorMessage = result.ErrorMessage,
            Artifacts = context.Artifacts,
            Logs = context.Logs,
            Warnings = result.Warnings,
            Phases = context.Phases,
            Elapsed = stopwatch.Elapsed,
            GlassBox = BuildGlassBox(glassBox, context),
        };
    }

    private static GlassBoxReport? BuildGlassBox(GlassBoxCapture? capture, LocalJobExecutionContext context)
    {
        if (capture is null)
        {
            return null;
        }

        var commands = capture.Commands;
        var previews = new List<ArtifactPreview>(context.Artifacts.Count);
        foreach (var artifact in context.Artifacts)
        {
            previews.Add(ArtifactPreview.FromArtifact(artifact));
        }

        var scratch = commands
            .Select(c => c.WorkingDirectory)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new GlassBoxReport
        {
            Commands = commands,
            Timeline = context.Phases,
            ArtifactPreviews = previews,
            ScratchDirectories = scratch,
        };
    }

    internal static ExecutionJobRecord BuildJobRecord(
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        IProcessExecutor executor)
    {
        // The local runner has no process catalog to consult, so the required runtime
        // profile is derived from the resolved executor's accepted set instead — a
        // native (GDAL) executor accepts the native profile, which the submit path's
        // catalog lookup would stamp as the same `native` value; a managed executor
        // leaves the profile null/default, exactly as the submit path does.
        var requiredRuntimeProfile = executor.AcceptedRuntimeProfiles.Contains(RuntimeProfiles.Native)
            ? RuntimeProfiles.Native
            : null;

        var operationId = "local-" + Guid.NewGuid().ToString("N");

        // The local runner is a single-step (processId + step-0 inputs) invocation;
        // model it as the canonical single-step plan the shared spec builder consumes.
        var plan = GeoprocessingSpecBuilder.SingleStepPlan(processId, inputs, planId: operationId);
        var spec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(
            plan,
            new Dictionary<string, string>(StringComparer.Ordinal),
            requiredRuntimeProfile);

        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = spec,
        };
    }
}
