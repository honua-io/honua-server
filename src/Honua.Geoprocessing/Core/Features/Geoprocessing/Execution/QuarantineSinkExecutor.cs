// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using Newtonsoft.Json;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class QuarantineSinkExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "sink.quarantine";

    private const string DefaultReasonField = "_quarantine_reason";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<QuarantineSinkExecutor> _logger;

    public QuarantineSinkExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<QuarantineSinkExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    // Test-friendly overload; production DI always resolves the logger.
    internal QuarantineSinkExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options)
        : this(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<QuarantineSinkExecutor>.Instance)
    {
    }

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
        await context.ReportProgressAsync(10, "Parsing quarantine inputs", cancellationToken).ConfigureAwait(false);

        var sinkRead = new FileSinkInputReader(HandledProcessId, _options.CurrentValue).Read(job, context);
        if (sinkRead.Rejection is { } inputRejection)
        {
            return inputRejection;
        }

        var read = sinkRead.Inputs!;
        var (path, outputRoot, resolvedPath, source) = (read.RequestedPath, read.OutputRoot, read.ResolvedPath, read.Source);
        var inputs = read.Inputs;

        var reasonField = inputs.GetOrDefault("reasonField", DefaultReasonField);
        var batchId = inputs.GetOrDefault("batchId", job.OperationId);

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Writing quarantine artifact", cancellationToken).ConfigureAwait(false);

        long quarantined = 0;
        try
        {
            await AtomicGeoJsonFeatureCollectionWriter.WriteAndCommitAsync(
                new GeoJsonFeatureCollectionWriteRequest
                {
                    ResolvedPath = resolvedPath,
                    Source = source,
                    Context = context,
                    WriteFeature = (geoJsonWriter, jsonWriter, feature) =>
                    {
                        var tagged = Tag(feature, batchId, reasonField);
                        try
                        {
                            geoJsonWriter.Write(tagged, jsonWriter);
                        }
                        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                        {
                            // Even the dead-letter write must not abort the job — record a placeholder.
                            geoJsonWriter.Write(Placeholder(batchId, reasonField, "Feature could not be serialized."), jsonWriter);
                        }

                        quarantined++;
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.SinkWriteFailed(_logger, job.OperationId, HandledProcessId, ex);
            return JobExecutionResult.Failed($"{HandledProcessId} write failed: {ex.GetType().Name}.");
        }

        // The rename committed an immutable output. Preserve its receipt even when
        // cancellation arrives after commit, independently of the cancelled job token.
        await context.RecordCommittedEffectAsync(
            SinkResultArtifact.Build(HandledProcessId,
                ("path", Path.GetRelativePath(outputRoot, resolvedPath)),
                ("requestedPath", path), ("attempt", job.AttemptCount), ("committed", true), ("featuresQuarantined", quarantined)),
            CancellationToken.None).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", CancellationToken.None).ConfigureAwait(false);

        return JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true };
    }

    private static Feature Tag(IFeature feature, string batchId, string reasonField)
    {
        var attributes = new AttributesTable();
        if (feature.Attributes is not null)
        {
            foreach (var name in feature.Attributes.GetNames())
            {
                attributes.Add(name, feature.Attributes.GetOptionalValue(name));
            }
        }

        if (!attributes.Exists("_batch_id"))
        {
            attributes.Add("_batch_id", batchId);
        }

        if (!attributes.Exists(reasonField))
        {
            attributes.Add(reasonField, "unspecified");
        }

        return new Feature(feature.Geometry, attributes);
    }

    private static Feature Placeholder(string batchId, string reasonField, string detail)
    {
        var attributes = new AttributesTable
        {
            { "_batch_id", batchId },
            { reasonField, $"serialization-failed: {detail}" }
        };
        return new Feature(null, attributes);
    }

    private static partial class Log
    {
        [LoggerMessage(9255, LogLevel.Error,
            "Sink executor failed job {OperationId} during {ProcessId} write")]
        public static partial void SinkWriteFailed(
            ILogger logger, string operationId, string processId, Exception exception);
    }
}
