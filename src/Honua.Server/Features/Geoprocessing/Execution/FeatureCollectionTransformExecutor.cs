// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Shared base for the GeoETL <c>transform.*</c> executors. Each transform reads
/// a <see cref="FeatureCollection"/> from the canonical <c>input</c> data URI
/// (<see cref="FeatureCollectionArtifact.DataUriPrefix"/>), applies an in-memory
/// NetTopologySuite transformation that carries feature attributes through, and
/// publishes a new FeatureCollection data URI. This is the #1185 add-a-capability
/// shape: the executor is the sole worker-side behavior for a single dotted
/// process id and surfaces automatically as a <c>process:&lt;id&gt;</c> workflow node.
/// </summary>
internal abstract class FeatureCollectionTransformExecutor : IJobExecutor
{
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;

    protected FeatureCollectionTransformExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// The single dotted process id this executor handles (e.g. <c>transform.reproject</c>).
    /// </summary>
    protected abstract string ProcessId { get; }

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        var resolved = GeoprocessingDispatchHelper.ResolveProcessId(parameters);
        if (!string.Equals(resolved, ProcessId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {ProcessId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, $"Parsing {ProcessId} inputs", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(parameters);
        if (!inputs.TryGetRequired("input", out var inputUri, out var inputError))
        {
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {inputError}");
        }

        if (!FeatureCollectionArtifact.TryParseDataUri(inputUri, out var source, out var parseError))
        {
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: 'input' {parseError}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, $"Applying {ProcessId}", cancellationToken).ConfigureAwait(false);

        List<IFeature> output;
        try
        {
            output = Apply(source, inputs, cancellationToken);
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JobExecutionResult.Failed($"{ProcessId} computation failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, $"Encoding {ProcessId} artifact", cancellationToken).ConfigureAwait(false);

        var payload = FeatureCollectionArtifact.WriteFeatureCollection(
            output,
            ProcessId,
            new[] { ("inputCount", (object)source.Count) });

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            return JobExecutionResult.Failed(
                $"{ProcessId} artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Reduce the input feature set before transforming.");
        }

        var artifactUri = FeatureCollectionArtifact.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{ProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    /// <summary>
    /// Applies the transform's algorithm to the parsed input feature set.
    /// Implementations return the output features, preserving attributes. Throw
    /// <see cref="TransformInputException"/> for caller-supplied parameter errors
    /// so they surface as a classified <c>Invalid ... inputs</c> failure.
    /// </summary>
    protected abstract List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken);
}

/// <summary>
/// Signals a caller-supplied parameter error inside a transform body. Mapped by
/// <see cref="FeatureCollectionTransformExecutor"/> to a classified job failure.
/// </summary>
internal sealed class TransformInputException(string message) : Exception(message);

/// <summary>
/// Thin reader over the durable spec parameter bag for the canonical first-step
/// (<c>0.</c>) inputs the geoprocessing submit path projects. Mirrors the prefix
/// the geometry executors read.
/// </summary>
internal readonly struct StepInputReader(IReadOnlyDictionary<string, string> parameters)
{
    private readonly string _prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

    public bool TryGet(string name, out string? value)
    {
        if (parameters.TryGetValue(_prefix + name, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = null;
        return false;
    }

    public bool TryGetRequired(string name, out string value, out string error)
    {
        if (TryGet(name, out var raw))
        {
            value = raw!;
            error = "";
            return true;
        }

        value = "";
        error = $"missing required input '{name}'";
        return false;
    }

    public string Require(string name)
    {
        if (TryGet(name, out var value))
        {
            return value!;
        }

        throw new TransformInputException($"missing required input '{name}'");
    }

    public string GetOrDefault(string name, string defaultValue)
        => TryGet(name, out var value) ? value! : defaultValue;
}
