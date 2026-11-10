// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class GeoJsonSourceExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options) : IProcessExecutor
{
    internal const string HandledProcessId = "source.geojson";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options = options;

    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

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
            if (!FeatureCollectionArtifact.TryParseDataUri(dataUri, out collection, out parseError, _options.CurrentValue.MaxArtifactBytes))
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

        // Publish through the spillable publisher: small sources stay inline (back-compat),
        // large sources spill to an NDJSON stream so downstream transforms read incrementally
        // and the legacy 50 MiB cap no longer fails a large source feed.
        var published = await FeatureStreamPublisher.PublishAsync(
            ToAsync(collection, cancellationToken),
            HandledProcessId,
            inputCount: collection.Count,
            _options.CurrentValue.OutputRootDirectory,
            job.OperationId,
            cancellationToken, executionContext: context).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(published.ArtifactReference, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static async IAsyncEnumerable<IFeature> ToAsync(
        FeatureCollection collection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var feature in collection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
