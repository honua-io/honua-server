// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// <c>sink.geojson-file</c> executor. Writes the input FeatureCollection to a GeoJSON
/// FeatureCollection file at the supplied <c>path</c> and publishes a small result
/// descriptor artifact (path + written/rejected counts). Managed NetTopologySuite
/// writer — no native dependency. Reconciled from the GeoETL baseline
/// GeoJsonFileSinkConnector onto the #1185 process/executor contract. Features with
/// null geometry are written (GeoJSON permits a null geometry member) but counted as
/// rejected so the result reflects them.
/// </summary>
internal sealed class GeoJsonFileSinkExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options) : IJobExecutor
{
    internal const string HandledProcessId = "sink.geojson-file";

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
        await context.ReportProgressAsync(10, "Parsing sink inputs", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!inputs.TryGetRequired("input", out var inputUri, out var inputError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {inputError}");
        }

        if (!inputs.TryGetRequired("path", out var path, out var pathError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {pathError}");
        }

        if (!FeatureCollectionArtifact.TryParseDataUri(inputUri, out var source, out var parseError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: 'input' {parseError}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Writing GeoJSON file", cancellationToken).ConfigureAwait(false);

        long written = 0;
        long rejected = 0;
        try
        {
            var geoJsonWriter = new GeoJsonWriter();
            await using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);
            await using var textWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var jsonWriter = new Newtonsoft.Json.JsonTextWriter(textWriter);

            await jsonWriter.WriteStartObjectAsync(cancellationToken).ConfigureAwait(false);
            await jsonWriter.WritePropertyNameAsync("type", cancellationToken).ConfigureAwait(false);
            await jsonWriter.WriteValueAsync("FeatureCollection", cancellationToken).ConfigureAwait(false);
            await jsonWriter.WritePropertyNameAsync("features", cancellationToken).ConfigureAwait(false);
            await jsonWriter.WriteStartArrayAsync(cancellationToken).ConfigureAwait(false);

            foreach (var feature in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var concrete = feature as Feature ?? new Feature(feature.Geometry, feature.Attributes);
                geoJsonWriter.Write(concrete, jsonWriter);

                if (feature.Geometry is null)
                {
                    rejected++;
                }
                else
                {
                    written++;
                }
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
            SinkResultArtifact.Build(HandledProcessId, ("path", path), ("featuresWritten", written), ("featuresRejected", rejected)),
            cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }
}

/// <summary>
/// Shared encoder for sink result-descriptor artifacts. Sinks terminate a workflow by
/// writing to an external target, so they publish a small JSON descriptor (the target
/// location and row counts) rather than a FeatureCollection.
/// </summary>
internal static class SinkResultArtifact
{
    public const string DataUriPrefix = "data:application/json;base64,";

    public static string Build(string processId, params (string Name, object Value)[] members)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("processId", processId);
            foreach (var (name, value) in members)
            {
                switch (value)
                {
                    case long l:
                        writer.WriteNumber(name, l);
                        break;
                    case int i:
                        writer.WriteNumber(name, i);
                        break;
                    case bool b:
                        writer.WriteBoolean(name, b);
                        break;
                    default:
                        writer.WriteString(name, value?.ToString() ?? string.Empty);
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return DataUriPrefix + Convert.ToBase64String(buffer.ToArray());
    }
}
