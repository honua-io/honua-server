// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>sink.quarantine</c> dead-letter executor. Writes every input feature to a companion
/// GeoJSON artifact at the supplied <c>path</c>, tagging each with the run batch id and an
/// optional rejection reason, and never throws on a malformed row — a single bad feature
/// is captured rather than failing the job. The sink half of the row-level-error contract:
/// rejected rows route here instead of aborting the run. Reconciled from the GeoETL
/// baseline QuarantineSinkConnector onto the #1185 process/executor contract.
/// </summary>
internal sealed class QuarantineSinkExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options) : IJobExecutor
{
    internal const string HandledProcessId = "sink.quarantine";

    private const string DefaultReasonField = "_quarantine_reason";

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
        await context.ReportProgressAsync(10, "Parsing quarantine inputs", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!inputs.TryGetRequired("input", out var inputUri, out var inputError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {inputError}");
        }

        if (!inputs.TryGetRequired("path", out var path, out var pathError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {pathError}");
        }

        if (!SinkPathResolver.TryResolve(_options.CurrentValue.SinkRootDirectory, path, out path, out var containmentError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: 'path' {containmentError}");
        }

        if (!FeatureCollectionArtifact.TryParseDataUri(inputUri, out var source, out var parseError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: 'input' {parseError}");
        }

        var reasonField = inputs.GetOrDefault("reasonField", DefaultReasonField);
        var batchId = inputs.GetOrDefault("batchId", job.OperationId);

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Writing quarantine artifact", cancellationToken).ConfigureAwait(false);

        long quarantined = 0;
        try
        {
            var geoJsonWriter = new GeoJsonWriter();
            await using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);
            await using var textWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var jsonWriter = new JsonTextWriter(textWriter);

            await jsonWriter.WriteStartObjectAsync(cancellationToken).ConfigureAwait(false);
            await jsonWriter.WritePropertyNameAsync("type", cancellationToken).ConfigureAwait(false);
            await jsonWriter.WriteValueAsync("FeatureCollection", cancellationToken).ConfigureAwait(false);
            await jsonWriter.WritePropertyNameAsync("features", cancellationToken).ConfigureAwait(false);
            await jsonWriter.WriteStartArrayAsync(cancellationToken).ConfigureAwait(false);

            foreach (var feature in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tagged = Tag(feature, batchId, reasonField);
                try
                {
                    geoJsonWriter.Write(tagged, jsonWriter);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                {
                    // Even the dead-letter write must not abort the job — record a placeholder.
                    geoJsonWriter.Write(Placeholder(batchId, reasonField, ex.Message), jsonWriter);
                }

                quarantined++;
            }

            await jsonWriter.WriteEndArrayAsync(cancellationToken).ConfigureAwait(false);
            await jsonWriter.WriteEndObjectAsync(cancellationToken).ConfigureAwait(false);
            await jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return JobExecutionResult.Failed($"{HandledProcessId} write failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(
            SinkResultArtifact.Build(HandledProcessId, ("path", path), ("featuresQuarantined", quarantined)),
            cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
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
}
