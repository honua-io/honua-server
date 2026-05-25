// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services.Connectors;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services;

/// <summary>
/// A progress / log observer the stage runner calls so the caller (the substrate
/// executor) can forward progress and structured logs through the per-job
/// <c>IJobExecutionContext</c> without the runner taking a substrate dependency.
/// </summary>
public interface IPipelineRunObserver
{
    /// <summary>
    /// Reports completion percentage and a phase label.
    /// </summary>
    /// <param name="percentComplete">Completion percentage (0-100), or null if indeterminate.</param>
    /// <param name="phase">Phase label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReportProgressAsync(double? percentComplete, string phase, CancellationToken cancellationToken);

    /// <summary>
    /// Appends an informational log line.
    /// </summary>
    /// <param name="message">Log message.</param>
    /// <param name="phase">Phase label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LogAsync(string message, string phase, CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of running a pipeline through the stage runner.
/// </summary>
public sealed record PipelineRunResult
{
    /// <summary>Number of features read from the source.</summary>
    public long FeaturesRead { get; init; }

    /// <summary>Number of features written by the sink (or counted by the dry-run sink).</summary>
    public long FeaturesWritten { get; init; }

    /// <summary>Number of features rejected by the sink (for example null geometry).</summary>
    public long FeaturesRejected { get; init; }

    /// <summary>Batch id tagged on every sink write for soft-delete rollback.</summary>
    public required string BatchId { get; init; }
}

/// <summary>
/// Executes a <see cref="PipelineDefinition"/> as a linear Source → []Transform → Sink
/// chain over a streaming <see cref="IAsyncEnumerable{IFeature}"/>. The runner is
/// substrate-agnostic: it reports progress and logs through <see cref="IPipelineRunObserver"/>
/// and is driven by the <c>PipelineJobExecutor</c> that the substrate's
/// <c>JobExecutionService</c> claims. Cancellation flows through every stage.
/// </summary>
public sealed class PipelineStageRunner(IPipelineComponentFactory factory)
{
    /// <summary>
    /// Runs the pipeline. When <paramref name="dryRun"/> is true the configured sink is
    /// replaced with the null preview sink so no durable target is written.
    /// </summary>
    /// <param name="definition">Pipeline definition to run.</param>
    /// <param name="batchId">Batch id tagged on every sink write.</param>
    /// <param name="dryRun">Whether to route the sink to the null preview sink.</param>
    /// <param name="observer">Optional progress / log observer.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The run result.</returns>
    public async Task<PipelineRunResult> RunAsync(
        PipelineDefinition definition,
        string batchId,
        bool dryRun,
        IPipelineRunObserver? observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(batchId);

        var sourceStage = definition.Source
            ?? throw new InvalidOperationException("Pipeline has no source stage.");
        var sinkStage = definition.Sink
            ?? throw new InvalidOperationException("Pipeline has no sink stage.");
        var sourceConfig = sourceStage.Connector
            ?? throw new InvalidOperationException("Source stage has no connector configuration.");
        var sinkConfig = sinkStage.Connector
            ?? throw new InvalidOperationException("Sink stage has no connector configuration.");

        var source = factory.ResolveSource(sourceConfig.Type)
            ?? throw new InvalidOperationException($"Source connector '{sourceConfig.Type}' is not registered.");

        IPipelineSinkConnector sink;
        if (dryRun)
        {
            sink = new NullPreviewSinkConnector();
        }
        else
        {
            sink = factory.ResolveSink(sinkConfig.Type)
                ?? throw new InvalidOperationException($"Sink connector '{sinkConfig.Type}' is not registered.");
        }

        await ReportAsync(observer, 5, "Extract", "pipeline.started", cancellationToken).ConfigureAwait(false);

        var readCounter = new FeatureCounter();
        IAsyncEnumerable<IFeature> stream = CountReads(source.ReadAsync(sourceConfig, cancellationToken), readCounter);

        var transformStages = definition.Transforms;
        for (var i = 0; i < transformStages.Count; i++)
        {
            var transformConfig = transformStages[i].Transform
                ?? throw new InvalidOperationException("Transform stage has no transform configuration.");
            var transform = factory.ResolveTransform(transformConfig.Type)
                ?? throw new InvalidOperationException($"Transform '{transformConfig.Type}' is not registered.");

            stream = transform.TransformAsync(transformConfig, stream, cancellationToken);
            await LogAsync(
                observer,
                $"Transform stage {i + 1}/{transformStages.Count}: {transformConfig.Type}",
                "pipeline.stage.completed",
                cancellationToken).ConfigureAwait(false);
        }

        await ReportAsync(observer, 50, "Load", "pipeline.stage.completed", cancellationToken).ConfigureAwait(false);

        var writeResult = await sink.WriteAsync(sinkConfig, stream, batchId, cancellationToken).ConfigureAwait(false);

        await ReportAsync(observer, 100, "Completed", "pipeline.completed", cancellationToken).ConfigureAwait(false);

        return new PipelineRunResult
        {
            FeaturesRead = readCounter.Count,
            FeaturesWritten = writeResult.FeaturesWritten,
            FeaturesRejected = writeResult.FeaturesRejected,
            BatchId = batchId
        };
    }

    private static async IAsyncEnumerable<IFeature> CountReads(
        IAsyncEnumerable<IFeature> source,
        FeatureCounter counter)
    {
        await foreach (var feature in source.ConfigureAwait(false))
        {
            counter.Count++;
            yield return feature;
        }
    }

    private static Task ReportAsync(
        IPipelineRunObserver? observer,
        double percent,
        string phase,
        string @event,
        CancellationToken cancellationToken)
        => observer is null
            ? Task.CompletedTask
            : Task.WhenAll(
                observer.ReportProgressAsync(percent, phase, cancellationToken),
                observer.LogAsync(@event, phase, cancellationToken));

    private static Task LogAsync(
        IPipelineRunObserver? observer,
        string message,
        string phase,
        CancellationToken cancellationToken)
        => observer?.LogAsync(message, phase, cancellationToken) ?? Task.CompletedTask;

    private sealed class FeatureCounter
    {
        public long Count;
    }
}
