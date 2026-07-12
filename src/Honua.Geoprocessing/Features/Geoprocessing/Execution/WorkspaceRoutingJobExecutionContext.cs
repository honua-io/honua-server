// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Decorates the durable <see cref="IJobExecutionContext"/> so that per-process
/// executors need no workspace-awareness of their own: when the job spec carries
/// a GP <c>env:workspace</c> request (<see cref="GeoprocessingProtocolMetadataKeys.GPServerWorkspace"/>),
/// every artifact an executor publishes is additionally routed into that
/// workspace through <see cref="IWorkspaceLifecycleService"/>, honoring
/// <c>env:overwriteOutput</c> collision semantics (<see cref="GeoprocessingProtocolMetadataKeys.GPServerOverwriteOutput"/>).
/// Installed by <see cref="GeoprocessingDispatchJobExecutor"/> — the single
/// funnel every geometry/transform/source/sink executor's published artifact
/// passes through — so this is the only place workspace routing needs to live.
/// </summary>
internal sealed class WorkspaceRoutingJobExecutionContext : IJobExecutionContext
{
    private readonly IJobExecutionContext _inner;
    private readonly ExecutionJobRecord _job;
    private readonly string _workspaceId;
    private readonly bool _overwrite;
    private readonly IWorkspaceLifecycleService _workspaceLifecycle;
    private int _publishedArtifactIndex;

    public WorkspaceRoutingJobExecutionContext(
        IJobExecutionContext inner,
        ExecutionJobRecord job,
        string workspaceId,
        bool overwrite,
        IWorkspaceLifecycleService workspaceLifecycle)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _job = job ?? throw new ArgumentNullException(nameof(job));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _workspaceId = workspaceId;
        _overwrite = overwrite;
        _workspaceLifecycle = workspaceLifecycle ?? throw new ArgumentNullException(nameof(workspaceLifecycle));
    }

    public string OperationId => _inner.OperationId;

    public Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default)
        => _inner.ReportProgressAsync(percentComplete, phase, cancellationToken);

    public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        => _inner.AppendLogAsync(entry, cancellationToken);

    /// <summary>
    /// Registers the artifact both on the durable job record (unchanged default
    /// behavior) and, when <c>env:workspace</c> was requested, as a workspace
    /// artifact keyed by the same stable output label GPServer/OGC Processes
    /// publish under (<c>process.output.{index}</c> / <c>gpserver.output.{index}</c>).
    /// A label collision throws <see cref="ArtifactAlreadyExistsException"/>
    /// when overwrite was not requested; <see cref="GeoprocessingDispatchJobExecutor"/>
    /// maps that into a curated <c>JobExecutionResult.Failed</c> so the caller sees
    /// a clear error instead of a generic execution failure.
    /// </summary>
    public async Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        var index = _publishedArtifactIndex++;
        var label = ResolveOutputLabel(_job, index);
        var kind = ResolveOutputKind(_job, index);

        // The durable publish is the gate: the real JobExecutionService rechecks
        // that the job still exists and is still owned, and can no-op/reject the
        // publish (lost lease, cancellation won). Delegate first so a rejected or
        // throwing inner publish leaves no workspace ledger entry for output that
        // was never durably recorded. Only after the inner publish is accepted do
        // we route the artifact into the requested env:workspace.
        await _inner.PublishArtifactAsync(artifactReference, cancellationToken).ConfigureAwait(false);

        await _workspaceLifecycle.AddOrReplaceArtifactAsync(
            _workspaceId,
            kind,
            label,
            _overwrite,
            uri: artifactReference,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveOutputLabel(ExecutionJobRecord job, int index)
        => job.Spec.Parameters.GetValueOrDefault($"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{index}")
            ?? job.Spec.Parameters.GetValueOrDefault($"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}{index}")
            ?? $"artifact{index + 1}";

    private static ArtifactKind ResolveOutputKind(ExecutionJobRecord job, int index)
    {
        if (job.Spec.Parameters.TryGetValue(
                ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds, out var serialized) &&
            !string.IsNullOrWhiteSpace(serialized))
        {
            var kinds = serialized.Split(
                ExecutionJobParameterKeys.MetadataListSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (index < kinds.Length &&
                Enum.TryParse<ArtifactKind>(kinds[index], ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }

        return ArtifactKind.File;
    }
}
