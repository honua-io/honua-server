// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// Deterministic certification executor registered only by the exact CITE profile.
/// It publishes bounded JSON data artifacts through the same worker context as every
/// production process; it does not own a job lifecycle or store.
/// </summary>
internal sealed class OgcProcessesCiteEchoExecutor : IProcessExecutor
{
    private const int MaximumPauseSeconds = 10;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;

    public OgcProcessesCiteEchoExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { OgcProcessesCiteEchoFixture.ProcessId };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        if (!OgcProcessesCiteEchoFixture.IsJob(job))
        {
            return JobExecutionResult.Failed("The CITE echo executor received a different process id.");
        }

        var inputPrefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        if (!OgcProcessesCiteEchoFixture.TryValidateCanonicalBinaryInput(
                job.Spec.Parameters.GetValueOrDefault(inputPrefix + "binary"),
                out var binaryError))
        {
            return JobExecutionResult.Failed(binaryError!);
        }

        if (!TryResolvePause(
                job.Spec.Parameters.GetValueOrDefault(inputPrefix + "pause"),
                out var pause,
                out var pauseError))
        {
            return JobExecutionResult.Failed(pauseError!);
        }

        await context.ReportProgressAsync(5, "Preparing deterministic CITE echo", cancellationToken)
            .ConfigureAwait(false);
        if (pause > TimeSpan.Zero)
        {
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }

        if (!OgcProcessesCiteEchoFixture.TryResolveOutputBindings(
                job.Spec.Parameters,
                out var outputIds))
        {
            return JobExecutionResult.Failed("The CITE echo job has invalid canonical output bindings.");
        }

        var maxArtifactBytes = _options.CurrentValue.MaxArtifactBytes;
        for (var index = 0; index < outputIds.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputId = outputIds[index];
            var rawInput = job.Spec.Parameters.GetValueOrDefault(inputPrefix + outputId);
            var payload = NormalizeJsonValue(rawInput);
            if (payload.Length > maxArtifactBytes)
            {
                return JobExecutionResult.Failed(
                    $"CITE echo output '{outputId}' exceeds MaxArtifactBytes={maxArtifactBytes}.");
            }

            var artifact = OgcProcessesCiteEchoFixture.DataUriPrefix + Convert.ToBase64String(payload);
            await context.PublishArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(
                    10 + (90d * (index + 1) / outputIds.Length),
                    $"Published deterministic CITE echo output {index + 1} of {outputIds.Length}",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return JobExecutionResult.Succeeded();
    }

    internal static bool TryDecodeArtifact(
        string? artifactUri,
        long maxArtifactBytes,
        out JsonElement value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(artifactUri)
            || maxArtifactBytes <= 0
            || !artifactUri.StartsWith(OgcProcessesCiteEchoFixture.DataUriPrefix, StringComparison.Ordinal)
            || artifactUri.Length - OgcProcessesCiteEchoFixture.DataUriPrefix.Length
                > ((maxArtifactBytes + 2L) / 3L * 4L))
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(
                artifactUri[OgcProcessesCiteEchoFixture.DataUriPrefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload.LongLength > maxArtifactBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] NormalizeJsonValue(string? rawInput)
    {
        if (rawInput is null)
        {
            return "null"u8.ToArray();
        }

        try
        {
            using var document = JsonDocument.Parse(rawInput);
            return Encoding.UTF8.GetBytes(document.RootElement.GetRawText());
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToUtf8Bytes(
                rawInput,
                OgcProcessesJsonContext.Default.String);
        }
    }

    private static bool TryResolvePause(string? rawPause, out TimeSpan pause, out string? error)
    {
        pause = TimeSpan.Zero;
        error = null;
        if (string.IsNullOrWhiteSpace(rawPause))
        {
            return true;
        }

        if (!int.TryParse(rawPause, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0
            || seconds > MaximumPauseSeconds)
        {
            error = $"CITE echo input 'pause' must be an integer from 0 through {MaximumPauseSeconds}.";
            return false;
        }

        pause = TimeSpan.FromSeconds(seconds);
        return true;
    }

}
