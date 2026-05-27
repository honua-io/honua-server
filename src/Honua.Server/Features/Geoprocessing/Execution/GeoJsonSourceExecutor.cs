// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// <c>source.geojson</c> executor. Parses an inline GeoJSON FeatureCollection (or one
/// supplied as the canonical FeatureCollection data URI) and publishes it as the
/// standard FeatureCollection artifact so downstream transform/sink nodes consume a
/// uniform envelope. Uses the managed NetTopologySuite GeoJSON reader — no native
/// dependency. Reconciled from the GeoETL baseline GeoJsonSourceConnector onto the
/// #1185 process/executor contract.
/// </summary>
/// <remarks>
/// The baseline connector streamed from a file <c>path</c> via the internal
/// StreamingGeoJsonReader. On the #1185 contract inputs are durable spec strings, so
/// this executor accepts an <c>inline</c> GeoJSON document or an <c>input</c> data URI.
/// File-path streaming for very large inputs belongs to a later streaming-source stream.
/// </remarks>
internal sealed class GeoJsonSourceExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options) : IJobExecutor
{
    internal const string HandledProcessId = "source.geojson";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options = options;

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var resolved = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (!string.Equals(resolved, HandledProcessId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(10, "Parsing GeoJSON source", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(job.Spec.Parameters);

        FeatureCollection collection;
        string parseError;
        if (inputs.TryGet("input", out var dataUri))
        {
            if (!FeatureCollectionArtifact.TryParseDataUri(dataUri, out collection, out parseError))
            {
                return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: 'input' {parseError}");
            }
        }
        else if (inputs.TryGet("inline", out var inline))
        {
            if (!FeatureCollectionArtifact.TryParseJson(inline, out collection, out parseError))
            {
                return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: 'inline' {parseError}");
            }
        }
        else
        {
            return JobExecutionResult.Failed(
                $"Invalid {HandledProcessId} inputs: requires an 'inline' GeoJSON document or an 'input' data URI.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(70, "Encoding source artifact", cancellationToken).ConfigureAwait(false);

        var payload = FeatureCollectionArtifact.WriteFeatureCollection(
            collection.ToList(),
            HandledProcessId);

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            return JobExecutionResult.Failed(
                $"{HandledProcessId} artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = FeatureCollectionArtifact.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }
}
