// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing.Inference;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>imagery.classify</c> process
/// (#2241) — the imagery/ML GP lane that DELEGATES inference to a configured
/// cloud endpoint instead of bundling a model runtime. The executor reads the
/// source GeoTIFF (inline, or materialized at submit time from a
/// <c>layerId</c>/<c>rasterId</c> catalog raster), submits it with the caller's
/// model reference to the configured <see cref="IImageryInferenceClient"/>
/// provider adapter, validates the returned classification raster or detected
/// features, and publishes the result as a standard GP data-URI artifact.
/// </summary>
/// <remarks>
/// Deployment honesty contract: when no backend is configured
/// (<c>Geoprocessing:ImageryInference:Provider</c> unset) the process stays
/// advertised in the catalog but every execution FAILS with a clear
/// "no cloud inference backend is configured" message — no silent stub, no fake
/// result (mirrors the <c>raster.interpolate-kriging</c> posture). Recognized
/// provider ids without a registered adapter (<c>sagemaker</c>, <c>vertex</c>,
/// <c>azureml</c>) also fail clearly, pointing at the generic <c>http</c>
/// adapter. Raster outputs are passed through byte-for-byte, so the
/// backend-preserved georeferencing (extent/CRS) lands intact in the artifact.
/// </remarks>
internal sealed partial class ImageryInferenceJobExecutor : IProcessExecutor
{
    /// <summary>The single process id this executor handles.</summary>
    internal const string HandledProcessId = "imagery.classify";

    /// <summary>Data-URI prefix used for GeoTIFF raster artifacts (matches the GDAL worker's).</summary>
    internal const string GeoTiffDataUriPrefix = "data:image/tiff; application=geotiff;base64,";

    /// <summary>
    /// Provider ids that are recognized configuration values but have no adapter
    /// in this build; they fail with a clear message instead of a generic
    /// "unknown provider" error.
    /// </summary>
    internal static readonly string[] RecognizedUnsupportedProviders = ["sagemaker", "vertex", "azureml"];

    private static readonly FrozenSet<string> AllowedTasks =
        new HashSet<string>(StringComparer.Ordinal) { "classification", "segmentation", "detection" }
            .ToFrozenSet(StringComparer.Ordinal);

    private readonly IOptionsMonitor<ImageryInferenceOptions> _inferenceOptions;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _executorOptions;
    private readonly FrozenDictionary<string, IImageryInferenceClient> _clients;
    private readonly ILogger<ImageryInferenceJobExecutor> _logger;

    public ImageryInferenceJobExecutor(
        IOptionsMonitor<ImageryInferenceOptions> inferenceOptions,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IEnumerable<IImageryInferenceClient> clients,
        ILogger<ImageryInferenceJobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(inferenceOptions);
        ArgumentNullException.ThrowIfNull(executorOptions);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(logger);

        _inferenceOptions = inferenceOptions;
        _executorOptions = executorOptions;
        _clients = clients.ToFrozenDictionary(c => c.Provider, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <summary>
    /// The single process id this executor handles, surfaced through
    /// <see cref="IProcessExecutor"/> so the dispatcher auto-registers it (#2122).
    /// </summary>
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

        var parameters = job.Spec.Parameters;
        var processId = GeoprocessingDispatchHelper.ResolveProcessId(parameters);

        if (!string.Equals(processId, HandledProcessId, StringComparison.Ordinal))
        {
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the imagery.classify executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Backend availability gate FIRST: an unconfigured deployment reports a
        // clear, actionable unavailability message before any input parsing noise.
        var options = _inferenceOptions.CurrentValue;
        if (!options.IsConfigured)
        {
            Log.BackendNotConfigured(_logger, job.OperationId);
            return JobExecutionResult.Failed(
                "imagery.classify is unavailable on this deployment: no cloud inference backend is configured. " +
                $"Set {ImageryInferenceOptions.SectionName}:Provider (supported: '{HttpImageryInferenceClient.ProviderId}' — " +
                "an OpenAI-compatible or hosted-ONNX REST endpoint) and " +
                $"{ImageryInferenceOptions.SectionName}:Endpoint, with credentials supplied as a secret reference or via the " +
                $"{ImageryInferenceOptions.ApiKeyEnvironmentVariable} environment variable.");
        }

        if (!_clients.TryGetValue(options.Provider, out var client))
        {
            if (RecognizedUnsupportedProviders.Contains(options.Provider, StringComparer.OrdinalIgnoreCase))
            {
                Log.ProviderUnsupported(_logger, job.OperationId, options.Provider);
                return JobExecutionResult.Failed(
                    $"imagery.classify inference provider '{options.Provider}' is recognized but not yet supported in this build. " +
                    $"Use provider '{HttpImageryInferenceClient.ProviderId}' pointed at the service's HTTPS invocation endpoint " +
                    "(OpenAI-compatible or hosted-ONNX REST) instead.");
            }

            Log.ProviderUnsupported(_logger, job.OperationId, options.Provider);
            return JobExecutionResult.Failed(
                $"imagery.classify inference provider '{options.Provider}' is not recognized. " +
                $"Supported: {HttpImageryInferenceClient.ProviderId}. Recognized but not yet supported: " +
                $"{string.Join(", ", RecognizedUnsupportedProviders)}.");
        }

        await context.ReportProgressAsync(5, "Parsing imagery inference inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadStepInputs(parameters, options, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid imagery.classify inputs: {inputError}");
        }

        var maxArtifactBytes = _executorOptions.CurrentValue.MaxArtifactBytes;
        var request = new ImageryInferenceRequest
        {
            Model = inputs.Model,
            Task = inputs.Task,
            ImageBytes = inputs.SourceBytes,
            ConfidenceThreshold = inputs.ConfidenceThreshold,
            MaxArtifactBytes = maxArtifactBytes
        };

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(20, "Delegating inference to the configured cloud backend", cancellationToken)
            .ConfigureAwait(false);

        ImageryInferenceOutcome outcome;
        try
        {
            outcome = await client.InferAsync(options, request, cancellationToken).ConfigureAwait(false);
        }
        catch (ImageryInferenceException ex)
        {
            // The adapter contract guarantees the message is safe for job status
            // (no endpoint, credentials, or raw provider bodies).
            Log.DelegationFailed(_logger, job.OperationId, options.Provider, ex);
            return JobExecutionResult.Failed($"imagery.classify inference failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Defensive: never leak unexpected provider internals to the caller.
            Log.DelegationFailed(_logger, job.OperationId, options.Provider, ex);
            return JobExecutionResult.Failed(
                $"imagery.classify inference failed unexpectedly ({ex.GetType().Name}).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Validating inference output", cancellationToken).ConfigureAwait(false);

        string artifactUri;
        if (outcome.OutputType == ImageryInferenceOutputType.Raster)
        {
            var rasterBytes = outcome.RasterBytes;
            if (rasterBytes is null || rasterBytes.Length == 0 || !LooksLikeTiff(rasterBytes))
            {
                return JobExecutionResult.Failed(
                    "imagery.classify inference failed: the backend returned a raster payload that is not a " +
                    "TIFF/GeoTIFF; the output raster must preserve the source georeferencing.");
            }

            if (rasterBytes.Length > maxArtifactBytes)
            {
                Log.ArtifactTooLarge(_logger, job.OperationId, rasterBytes.Length, maxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"imagery.classify output raster size {rasterBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={maxArtifactBytes}. Reduce the scene size or raise the limit.");
            }

            artifactUri = GeoTiffDataUriPrefix + Convert.ToBase64String(rasterBytes);
        }
        else
        {
            var featureJson = outcome.FeatureCollectionJson;
            if (featureJson is null || featureJson.Length == 0 || !IsFeatureCollection(featureJson))
            {
                return JobExecutionResult.Failed(
                    "imagery.classify inference failed: the backend returned a features payload that is not a " +
                    "GeoJSON FeatureCollection.");
            }

            if (featureJson.Length > maxArtifactBytes)
            {
                Log.ArtifactTooLarge(_logger, job.OperationId, featureJson.Length, maxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"imagery.classify output feature collection size {featureJson.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={maxArtifactBytes}. Raise the limit or apply a stricter confidenceThreshold.");
            }

            artifactUri = FeatureCollectionArtifact.BuildDataUri(featureJson);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Imagery inference completed", cancellationToken).ConfigureAwait(false);

        Log.InferenceSucceeded(_logger, job.OperationId, options.Provider, outcome.OutputType.ToString());
        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadStepInputs(
        IReadOnlyDictionary<string, string> parameters,
        ImageryInferenceOptions options,
        out InferenceInputs inputs,
        out string error)
    {
        inputs = default!;
        error = "";

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "source", out var source) || string.IsNullOrWhiteSpace(source))
        {
            error = "missing required input 'source'; supply an inline base64 GeoTIFF or a layerId/rasterId " +
                "that resolves to a registered catalog raster at submit time";
            return false;
        }

        byte[] sourceBytes;
        try
        {
            sourceBytes = Convert.FromBase64String(source);
        }
        catch (FormatException)
        {
            error = "input 'source' is not valid base64";
            return false;
        }

        if (sourceBytes.Length == 0 || !LooksLikeTiff(sourceBytes))
        {
            error = "input 'source' is not a TIFF/GeoTIFF payload";
            return false;
        }

        var model = parameters.GetValueOrDefault(prefix + "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            model = options.DefaultModel;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            error = "missing required input 'model'; supply a model reference understood by the configured backend " +
                $"(or configure {ImageryInferenceOptions.SectionName}:DefaultModel)";
            return false;
        }

        var task = parameters.GetValueOrDefault(prefix + "task");
        if (string.IsNullOrWhiteSpace(task))
        {
            task = "classification";
        }

        if (!AllowedTasks.Contains(task))
        {
            error = $"invalid input 'task'; expected one of classification, segmentation, detection, got '{task}'";
            return false;
        }

        double? confidenceThreshold = null;
        if (parameters.TryGetValue(prefix + "confidenceThreshold", out var thresholdRaw)
            && !string.IsNullOrWhiteSpace(thresholdRaw))
        {
            if (!double.TryParse(thresholdRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || double.IsNaN(parsed) || parsed < 0d || parsed > 1d)
            {
                error = $"invalid input 'confidenceThreshold'; expected a number between 0 and 1, got '{thresholdRaw}'";
                return false;
            }

            confidenceThreshold = parsed;
        }

        inputs = new InferenceInputs(sourceBytes, model.Trim(), task, confidenceThreshold);
        return true;
    }

    /// <summary>
    /// Cheap honesty check on raster payloads: classic TIFF (II*\0 / MM\0*) or
    /// BigTIFF (II+\0 / MM\0+) magic. A backend that answers with PNG/JPEG (no
    /// georeferencing) is rejected instead of being landed as a "GeoTIFF" artifact.
    /// </summary>
    private static bool LooksLikeTiff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        var littleEndian = bytes[0] == 0x49 && bytes[1] == 0x49 && (bytes[2] == 0x2A || bytes[2] == 0x2B) && bytes[3] == 0x00;
        var bigEndian = bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && (bytes[3] == 0x2A || bytes[3] == 0x2B);
        return littleEndian || bigEndian;
    }

    private static bool IsFeatureCollection(byte[] utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "FeatureCollection", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record InferenceInputs(
        byte[] SourceBytes,
        string Model,
        string Task,
        double? ConfidenceThreshold);

    private static partial class Log
    {
        [LoggerMessage(9313, LogLevel.Warning,
            "Imagery inference executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9314, LogLevel.Warning,
            "Imagery inference executor failed job {OperationId}: no cloud inference backend is configured")]
        public static partial void BackendNotConfigured(ILogger logger, string operationId);

        [LoggerMessage(9315, LogLevel.Warning,
            "Imagery inference executor failed job {OperationId}: provider '{Provider}' has no registered adapter")]
        public static partial void ProviderUnsupported(ILogger logger, string operationId, string provider);

        [LoggerMessage(9316, LogLevel.Warning,
            "Imagery inference executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9317, LogLevel.Warning,
            "Imagery inference executor failed job {OperationId} delegating to provider '{Provider}'")]
        public static partial void DelegationFailed(ILogger logger, string operationId, string provider, Exception exception);

        [LoggerMessage(9318, LogLevel.Warning,
            "Imagery inference executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9319, LogLevel.Information,
            "Imagery inference executor completed job {OperationId} via provider '{Provider}' with {OutputType} output")]
        public static partial void InferenceSucceeded(ILogger logger, string operationId, string provider, string outputType);
    }
}
